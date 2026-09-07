using System.Security.Cryptography;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private const int MaxRecoveryRunbookRuns = 2_000;
    private const int MaxMeshCentralSyncEvents = 50_000;
    private static readonly string[] IntegrationPurposes = ["meshcentral-status"];

    public async Task<RecoveryRunbookRecord> CreateRecoveryRunbookAsync(string name, IReadOnlyList<Guid> recoveryPlanIds, int rtoBudgetMinutes, int intervalDays, bool enabled, CancellationToken ct)
    {
        var normalizedName = NormalizeFabricName(name, "Recovery runbook name");
        var ids = (recoveryPlanIds ?? []).Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length is < 1 or > 100) throw new ArgumentException("Recovery runbook must contain 1..100 unique recovery plans.", nameof(recoveryPlanIds));
        if (rtoBudgetMinutes is < 1 or > 10080) throw new ArgumentOutOfRangeException(nameof(rtoBudgetMinutes));
        if (intervalDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(intervalDays));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ids.Any(id => !_state.RecoveryPlans.ContainsKey(id))) throw new KeyNotFoundException("One or more recovery plans were not found.");
            var now = DateTimeOffset.UtcNow;
            var record = new RecoveryRunbookRecord(Guid.NewGuid(), normalizedName, ids, rtoBudgetMinutes, intervalDays, enabled, now, null, now);
            var next = CloneState();
            next.RecoveryRunbooks[record.RunbookId] = record;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RecoveryRunbookRecord>> GetRecoveryRunbooksAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.RecoveryRunbooks.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RecoveryRunbookRunRecord>> GetRecoveryRunbookRunsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.RecoveryRunbookRuns.Values.OrderByDescending(x => x.StartedAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<int> AdvanceProductionFabricAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            var changed = false;
            var advances = 0;

            foreach (var runbook in next.RecoveryRunbooks.Values.Where(x => x.Enabled && x.NextRunAtUtc <= nowUtc).OrderBy(x => x.NextRunAtUtc).ToArray())
            {
                if (next.RecoveryRunbookRuns.Values.Any(x => x.RunbookId == runbook.RunbookId && x.CompletedAtUtc is null)) continue;
                var steps = runbook.RecoveryPlanIds.Select((planId, index) => new RecoveryRunbookStepRecord(index, planId, null, "pending", null, null, null)).ToArray();
                var run = new RecoveryRunbookRunRecord(Guid.NewGuid(), runbook.RunbookId, nowUtc, null, "running", runbook.RtoBudgetMinutes, 0, steps);
                next.RecoveryRunbookRuns[run.RunId] = run;
                var following = runbook.NextRunAtUtc;
                do { following = following.AddDays(runbook.IntervalDays); } while (following <= nowUtc);
                next.RecoveryRunbooks[runbook.RunbookId] = runbook with { LastRunAtUtc = nowUtc, NextRunAtUtc = following };
                changed = true;
                advances++;
            }

            foreach (var original in next.RecoveryRunbookRuns.Values.Where(x => x.CompletedAtUtc is null).OrderBy(x => x.StartedAtUtc).ToArray())
            {
                var run = original;
                if (nowUtc - run.StartedAtUtc > TimeSpan.FromMinutes(run.RtoBudgetMinutes))
                {
                    var timedOutSteps = run.Steps.Select(s => s.Status is "succeeded" or "failed" ? s : s with { Status = "failed", CompletedAtUtc = nowUtc, Error = "Recovery runbook RTO budget exceeded." }).ToArray();
                    next.RecoveryRunbookRuns[run.RunId] = run with { CompletedAtUtc = nowUtc, Status = "failed", Steps = timedOutSteps };
                    changed = true;
                    advances++;
                    continue;
                }

                var steps = run.Steps.ToArray();
                var currentIndex = Math.Clamp(run.CurrentStepIndex, 0, Math.Max(0, steps.Length - 1));
                if (steps.Length == 0)
                {
                    next.RecoveryRunbookRuns[run.RunId] = run with { CompletedAtUtc = nowUtc, Status = "failed" };
                    changed = true;
                    continue;
                }

                var step = steps[currentIndex];
                if (step.Status == "pending")
                {
                    if (!next.RecoveryPlans.TryGetValue(step.RecoveryPlanId, out var plan))
                    {
                        steps[currentIndex] = step with { Status = "failed", CompletedAtUtc = nowUtc, Error = "Recovery plan no longer exists." };
                        next.RecoveryRunbookRuns[run.RunId] = run with { CompletedAtUtc = nowUtc, Status = "failed", Steps = steps };
                        changed = true;
                        advances++;
                        continue;
                    }

                    var targets = plan.PolicyIds.Select(policyId =>
                    {
                        if (!next.BackupPolicies.TryGetValue(policyId, out var policy))
                            return new RecoveryRunTargetRecord(Guid.NewGuid(), policyId, Guid.Empty, null, "failed", false, 0, 0, "Backup policy no longer exists.");
                        return new RecoveryRunTargetRecord(Guid.NewGuid(), policyId, policy.AgentId, null, "pending", null, 0, 0, null);
                    }).ToArray();
                    var recoveryRun = new RecoveryRunRecord(Guid.NewGuid(), plan.PlanId, nowUtc, null, "running", plan.RtoTargetMinutes, 0, 0, targets);
                    next.RecoveryRuns[recoveryRun.RunId] = recoveryRun;
                    steps[currentIndex] = step with { RecoveryRunId = recoveryRun.RunId, Status = "running", StartedAtUtc = nowUtc };
                    next.RecoveryRunbookRuns[run.RunId] = run with { Steps = steps };
                    changed = true;
                    advances++;
                    continue;
                }

                if (step.Status == "running" && step.RecoveryRunId is { } recoveryRunId && next.RecoveryRuns.TryGetValue(recoveryRunId, out var matchedRecoveryRun) && matchedRecoveryRun.CompletedAtUtc is not null)
                {
                    var succeeded = string.Equals(matchedRecoveryRun.Status, "succeeded", StringComparison.Ordinal);
                    steps[currentIndex] = step with { Status = succeeded ? "succeeded" : "failed", CompletedAtUtc = matchedRecoveryRun.CompletedAtUtc, Error = succeeded ? null : "Recovery plan drill failed or exceeded its RTO." };
                    if (!succeeded)
                    {
                        next.RecoveryRunbookRuns[run.RunId] = run with { CompletedAtUtc = nowUtc, Status = "failed", Steps = steps };
                    }
                    else if (currentIndex + 1 >= steps.Length)
                    {
                        next.RecoveryRunbookRuns[run.RunId] = run with { CompletedAtUtc = nowUtc, Status = "succeeded", CurrentStepIndex = currentIndex, Steps = steps };
                    }
                    else
                    {
                        next.RecoveryRunbookRuns[run.RunId] = run with { CurrentStepIndex = currentIndex + 1, Steps = steps };
                    }
                    changed = true;
                    advances++;
                }
            }

            PruneProductionFabricUnsafe(next);
            if (changed) await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return advances;
        }
        finally { _gate.Release(); }
    }

    public async Task<(IntegrationCredentialRecord Record, string PlaintextToken)> CreateIntegrationCredentialAsync(string name, string purpose, TimeSpan validity, CancellationToken ct)
    {
        var normalizedName = NormalizeFabricName(name, "Integration credential name");
        var normalizedPurpose = (purpose ?? string.Empty).Trim().ToLowerInvariant();
        if (!IntegrationPurposes.Contains(normalizedPurpose, StringComparer.Ordinal)) throw new ArgumentException("Integration credential purpose is not supported.", nameof(purpose));
        if (validity < TimeSpan.FromDays(1) || validity > TimeSpan.FromDays(365)) throw new ArgumentOutOfRangeException(nameof(validity));
        var token = "ybit_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var record = new IntegrationCredentialRecord(Guid.NewGuid(), normalizedName, normalizedPurpose, HashToken(token), now, now.Add(validity), null, false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            next.IntegrationCredentials[record.CredentialId] = record;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return (record, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<IntegrationCredentialRecord?> AuthenticateIntegrationCredentialAsync(string plaintextToken, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken) || !plaintextToken.StartsWith("ybit_", StringComparison.Ordinal)) return null;
        var normalizedPurpose = (purpose ?? string.Empty).Trim().ToLowerInvariant();
        var tokenHash = HashToken(plaintextToken);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var match = _state.IntegrationCredentials.Values.FirstOrDefault(x => !x.Revoked && x.ExpiresAtUtc > now && string.Equals(x.Purpose, normalizedPurpose, StringComparison.Ordinal) && FixedTimeEquals(x.TokenHash, tokenHash));
            if (match is null) return null;
            var updated = match with { LastUsedAtUtc = now };
            var next = CloneState();
            next.IntegrationCredentials[match.CredentialId] = updated;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<IntegrationCredentialRecord>> GetIntegrationCredentialsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.IntegrationCredentials.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeIntegrationCredentialAsync(Guid credentialId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.IntegrationCredentials.TryGetValue(credentialId, out var record)) return false;
            if (record.Revoked) return true;
            var next = CloneState();
            next.IntegrationCredentials[credentialId] = record with { Revoked = true };
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralSyncEventRecord> ReportMeshCentralSyncAsync(Guid credentialId, Guid eventId, Guid agentId, string nodeId, string nodeStatus, string? deploymentStatus, DateTimeOffset reportedAtUtc, DateTimeOffset receivedAtUtc, CancellationToken ct)
    {
        if (credentialId == Guid.Empty || eventId == Guid.Empty || agentId == Guid.Empty) throw new ArgumentException("Credential, event and agent ids are required.");
        var normalizedNode = NormalizeOpaqueNodeId(nodeId);
        var normalizedNodeStatus = NormalizeEnumText(nodeStatus, "unknown", ["unknown", "online", "offline", "warning"], nameof(nodeStatus));
        var normalizedDeployment = string.IsNullOrWhiteSpace(deploymentStatus) ? null : NormalizeEnumText(deploymentStatus, "unknown", ["unknown", "installed", "updating", "failed", "missing"], nameof(deploymentStatus));
        if (reportedAtUtc > receivedAtUtc.AddMinutes(5) || reportedAtUtc < receivedAtUtc.AddDays(-7)) throw new ArgumentOutOfRangeException(nameof(reportedAtUtc), "Reported time is outside the accepted replay window.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.IntegrationCredentials.TryGetValue(credentialId, out var credential) || credential.Revoked || credential.ExpiresAtUtc <= receivedAtUtc || credential.Purpose != "meshcentral-status")
                throw new UnauthorizedAccessException("Integration credential is not active.");
            if (!_state.MeshCentralLinks.TryGetValue(agentId, out var link)) throw new KeyNotFoundException("MeshCentral link not found for agent.");
            if (!string.Equals(link.NodeId, normalizedNode, StringComparison.Ordinal)) throw new UnauthorizedAccessException("MeshCentral node id does not match the registered agent link.");
            if (_state.MeshCentralSyncEvents.TryGetValue(eventId, out var existing))
            {
                if (existing.AgentId != agentId || existing.CredentialId != credentialId || !string.Equals(existing.NodeId, normalizedNode, StringComparison.Ordinal))
                    throw new InvalidOperationException("Integration event id was already used with different attributes.");
                return existing;
            }

            var record = new MeshCentralSyncEventRecord(eventId, agentId, normalizedNode, normalizedNodeStatus, normalizedDeployment, reportedAtUtc, receivedAtUtc, credentialId);
            var next = CloneState();
            next.MeshCentralSyncEvents[eventId] = record;
            next.MeshCentralLinks[agentId] = link with { LastSynchronizedAtUtc = receivedAtUtc, LastKnownNodeStatus = normalizedNodeStatus, LastDeploymentStatus = normalizedDeployment };
            PruneProductionFabricUnsafe(next);
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MeshCentralSyncEventRecord>> GetMeshCentralSyncEventsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 5000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.MeshCentralSyncEvents.Values.OrderByDescending(x => x.ReceivedAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }

    private static string NormalizeFabricName(string name, string label)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length is < 3 or > 128 || value.Any(char.IsControl)) throw new ArgumentException($"{label} is invalid.", nameof(name));
        return value;
    }

    private static string NormalizeOpaqueNodeId(string nodeId)
    {
        var value = (nodeId ?? string.Empty).Trim();
        if (value.Length is < 1 or > 512 || value.Any(char.IsControl)) throw new ArgumentException("MeshCentral node id is invalid.", nameof(nodeId));
        return value;
    }

    private static string NormalizeEnumText(string? value, string fallback, IReadOnlyCollection<string> allowed, string parameter)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        if (!allowed.Contains(normalized, StringComparer.Ordinal)) throw new ArgumentException("Status value is invalid.", parameter);
        return normalized;
    }

    private static void PruneProductionFabricUnsafe(StateDocument state)
    {
        foreach (var id in state.RecoveryRunbookRuns.Values.OrderByDescending(x => x.StartedAtUtc).Skip(MaxRecoveryRunbookRuns).Select(x => x.RunId).ToArray()) state.RecoveryRunbookRuns.Remove(id);
        foreach (var id in state.MeshCentralSyncEvents.Values.OrderByDescending(x => x.ReceivedAtUtc).Skip(MaxMeshCentralSyncEvents).Select(x => x.EventId).ToArray()) state.MeshCentralSyncEvents.Remove(id);
    }
}
