using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private const int MaxRepositoryHealthRecords = 10_000;
    private const int MaxRecoveryRuns = 2_000;
    private const int MaxNotificationDeliveries = 20_000;

    public async Task<IReadOnlyList<RepositoryHealthRecord>> GetRepositoryHealthAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 5000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.RepositoryHealth.Values.OrderByDescending(x => x.MeasuredAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<RecoveryPlanRecord> CreateRecoveryPlanAsync(string name, IReadOnlyList<Guid> policyIds, int maxParallelAgents, int rtoTargetMinutes, int intervalDays, bool enabled, CancellationToken ct)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length is < 3 or > 128 || normalizedName.Any(char.IsControl)) throw new ArgumentException("Recovery plan name is invalid.", nameof(name));
        var ids = (policyIds ?? []).Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length is < 1 or > 250) throw new ArgumentException("Recovery plan must contain 1..250 unique policy ids.", nameof(policyIds));
        if (maxParallelAgents is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(maxParallelAgents));
        if (rtoTargetMinutes is < 1 or > 10080) throw new ArgumentOutOfRangeException(nameof(rtoTargetMinutes));
        if (intervalDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(intervalDays));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ids.Any(id => !_state.BackupPolicies.ContainsKey(id))) throw new KeyNotFoundException("One or more backup policies were not found.");
            var now = DateTimeOffset.UtcNow;
            var plan = new RecoveryPlanRecord(Guid.NewGuid(), normalizedName, ids, maxParallelAgents, rtoTargetMinutes, intervalDays, enabled, now, null, now);
            var next = CloneState();
            next.RecoveryPlans[plan.PlanId] = plan;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return plan;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RecoveryPlanRecord>> GetRecoveryPlansAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.RecoveryPlans.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RecoveryRunRecord>> GetRecoveryRunsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.RecoveryRuns.Values.OrderByDescending(x => x.StartedAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<int> EnqueueDueResilienceAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            var enqueued = 0;
            var changed = false;

            foreach (var original in next.BackupPolicies.Values.Where(x => x.Enabled).ToArray())
            {
                var policy = original;
                var healthHours = policy.RepositoryHealthIntervalHours is >= 1 and <= 720 ? policy.RepositoryHealthIntervalHours : 24;
                var nextHealth = policy.NextRepositoryHealthAtUtc ?? nowUtc;
                if (nextHealth <= nowUtc && PendingCount(next, policy.AgentId) < MaxPendingCommandsPerAgent)
                {
                    var payload = new RepositoryHealthScanPayload(policy.PolicyId, policy.RepositoryRoot, policy.RepositoryId);
                    var key = $"policy:{policy.PolicyId:N}:repository-health:{nextHealth.UtcDateTime.Ticks}";
                    var command = NewCommand(policy.AgentId, AgentCommandType.RepositoryHealthScan, JsonSerializer.Serialize(payload), key, nowUtc);
                    next.Commands[command.CommandId] = command;
                    var following = nextHealth;
                    do { following = following.AddHours(healthHours); } while (following <= nowUtc);
                    policy = policy with { RepositoryHealthIntervalHours = healthHours, LastRepositoryHealthScheduledAtUtc = nowUtc, NextRepositoryHealthAtUtc = following };
                    next.BackupPolicies[policy.PolicyId] = policy;
                    enqueued++;
                    changed = true;
                }
            }

            foreach (var plan in next.RecoveryPlans.Values.Where(x => x.Enabled && x.NextRunAtUtc <= nowUtc).OrderBy(x => x.NextRunAtUtc).ToArray())
            {
                var alreadyRunning = next.RecoveryRuns.Values.Any(x => x.PlanId == plan.PlanId && x.CompletedAtUtc is null);
                if (alreadyRunning) continue;
                var targets = plan.PolicyIds.Select(policyId =>
                {
                    if (!next.BackupPolicies.TryGetValue(policyId, out var policy))
                        return new RecoveryRunTargetRecord(Guid.NewGuid(), policyId, Guid.Empty, null, "failed", false, 0, 0, "Backup policy no longer exists.");
                    return new RecoveryRunTargetRecord(Guid.NewGuid(), policyId, policy.AgentId, null, "pending", null, 0, 0, null);
                }).ToArray();
                var run = new RecoveryRunRecord(Guid.NewGuid(), plan.PlanId, nowUtc, null, "running", plan.RtoTargetMinutes, 0, 0, targets);
                next.RecoveryRuns[run.RunId] = run;
                var nextRun = plan.NextRunAtUtc;
                do { nextRun = nextRun.AddDays(plan.IntervalDays); } while (nextRun <= nowUtc);
                next.RecoveryPlans[plan.PlanId] = plan with { LastRunAtUtc = nowUtc, NextRunAtUtc = nextRun };
                changed = true;
            }

            foreach (var run in next.RecoveryRuns.Values.Where(x => x.CompletedAtUtc is null).OrderBy(x => x.StartedAtUtc).ToArray())
            {
                if (!next.RecoveryPlans.TryGetValue(run.PlanId, out var plan)) continue;
                var targets = run.Targets.ToArray();
                var active = targets.Count(x => x.Status == "running");
                for (var i = 0; i < targets.Length && active < plan.MaxParallelAgents; i++)
                {
                    var target = targets[i];
                    if (target.Status != "pending") continue;
                    if (!next.BackupPolicies.TryGetValue(target.PolicyId, out var policy))
                    {
                        targets[i] = target with { Status = "failed", Succeeded = false, Error = "Backup policy no longer exists." };
                        changed = true;
                        continue;
                    }
                    if (PendingCount(next, policy.AgentId) >= MaxPendingCommandsPerAgent) continue;
                    var payload = new RestoreDrillPayload(policy.RepositoryRoot, policy.RepositoryId, policy.SourcePath);
                    var key = $"recovery:{run.RunId:N}:target:{target.TargetId:N}";
                    var command = NewCommand(policy.AgentId, AgentCommandType.RestoreDrill, JsonSerializer.Serialize(payload), key, nowUtc);
                    next.Commands[command.CommandId] = command;
                    targets[i] = target with { CommandId = command.CommandId, Status = "running" };
                    active++;
                    enqueued++;
                    changed = true;
                }
                var updatedRun = FinalizeRecoveryRunIfTerminal(run with { Targets = targets }, nowUtc);
                next.RecoveryRuns[run.RunId] = updatedRun;
            }

            PruneResilienceStateUnsafe(next);
            if (changed) await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return enqueued;
        }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralLinkRecord> UpsertMeshCentralLinkAsync(Guid agentId, string baseUri, string nodeId, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("MeshCentral base URI must be an absolute HTTPS URI without userinfo.", nameof(baseUri));
        var normalizedNode = (nodeId ?? string.Empty).Trim();
        if (normalizedNode.Length is < 1 or > 512 || normalizedNode.Any(char.IsControl)) throw new ArgumentException("MeshCentral node id is invalid.", nameof(nodeId));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.ContainsKey(agentId)) throw new KeyNotFoundException("Agent not found.");
            var now = DateTimeOffset.UtcNow;
            var previous = _state.MeshCentralLinks.GetValueOrDefault(agentId);
            var record = new MeshCentralLinkRecord(agentId, uri.GetLeftPart(UriPartial.Authority), normalizedNode, previous?.LinkedAtUtc ?? now, previous?.LastSynchronizedAtUtc, previous?.LastKnownNodeStatus, previous?.LastDeploymentStatus);
            var next = CloneState();
            next.MeshCentralLinks[agentId] = record;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MeshCentralLinkRecord>> GetMeshCentralLinksAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.MeshCentralLinks.Values.OrderBy(x => x.AgentId).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<NotificationRouteRecord> CreateNotificationRouteAsync(NotificationRouteRecord route, CancellationToken ct)
    {
        if (route.RouteId == Guid.Empty) throw new ArgumentException("Route id is required.", nameof(route));
        if (string.IsNullOrWhiteSpace(route.ProtectedHmacSecret)) throw new ArgumentException("Protected HMAC secret is required.", nameof(route));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            next.NotificationRoutes[route.RouteId] = route;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return route;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NotificationRouteRecord>> GetNotificationRoutesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.NotificationRoutes.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteNotificationRouteAsync(Guid routeId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.NotificationRoutes.ContainsKey(routeId)) return false;
            var next = CloneState();
            next.NotificationRoutes.Remove(routeId);
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NotificationDeliveryRecord>> PrepareNotificationDeliveriesAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            var changed = false;
            foreach (var route in next.NotificationRoutes.Values.Where(x => x.Enabled))
            {
                foreach (var alarm in next.Alarms.Values.Where(x => x.Status != AlarmStatus.Resolved && route.Severities.Contains(x.Severity, StringComparer.OrdinalIgnoreCase)))
                {
                    var exists = next.NotificationDeliveries.Values.Any(x => x.RouteId == route.RouteId && x.AlarmId == alarm.AlarmId && x.AlarmVersionUtc == alarm.LastSeenAtUtc);
                    if (exists) continue;
                    var delivery = new NotificationDeliveryRecord(Guid.NewGuid(), route.RouteId, alarm.AlarmId, alarm.LastSeenAtUtc, nowUtc, null, 0, false, null);
                    next.NotificationDeliveries[delivery.DeliveryId] = delivery;
                    changed = true;
                }
            }
            PruneResilienceStateUnsafe(next);
            if (changed) await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return next.NotificationDeliveries.Values.Where(x => x.CompletedAtUtc is null && x.AttemptCount < 3).OrderBy(x => x.CreatedAtUtc).Take(100).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteNotificationDeliveryAsync(Guid deliveryId, bool succeeded, string? errorMessage, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.NotificationDeliveries.TryGetValue(deliveryId, out var current) || current.CompletedAtUtc is not null) return;
            var attempt = current.AttemptCount + 1;
            var complete = succeeded || attempt >= 3;
            var next = CloneState();
            next.NotificationDeliveries[deliveryId] = current with { AttemptCount = attempt, Succeeded = succeeded, Error = succeeded ? null : TrimError(errorMessage), CompletedAtUtc = complete ? nowUtc : null };
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NotificationDeliveryRecord>> GetNotificationDeliveriesAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.NotificationDeliveries.Values.OrderByDescending(x => x.CreatedAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }

    private static string? TrimError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var value = error.Trim();
        return value.Length <= 512 ? value : value[..512];
    }

    private static RecoveryRunRecord FinalizeRecoveryRunIfTerminal(RecoveryRunRecord run, DateTimeOffset nowUtc)
    {
        if (run.Targets.Any(x => x.Status is "pending" or "running")) return run;
        var longest = run.Targets.Count == 0 ? 0 : run.Targets.Max(x => x.DurationMilliseconds);
        var bytes = run.Targets.Sum(x => x.VerifiedBytes);
        var withinRto = longest <= run.RtoTargetMinutes * 60_000L;
        var success = run.Targets.Count > 0 && run.Targets.All(x => x.Succeeded == true) && withinRto;
        return run with { CompletedAtUtc = nowUtc, Status = success ? "succeeded" : "failed", VerifiedBytes = bytes, LongestRestoreMilliseconds = longest };
    }

    private static void ApplyResilienceCommandResultUnsafe(StateDocument state, AgentCommand completed, string resultJson, string? error, DateTimeOffset completedAt)
    {
        if (completed.Type == AgentCommandType.RepositoryHealthScan)
        {
            if (!completed.Succeeded) return;
            try
            {
                var dto = JsonSerializer.Deserialize<RepositoryHealthScanResultDto>(resultJson);
                if (dto is null) return;
                var record = new RepositoryHealthRecord(Guid.NewGuid(), dto.PolicyId, completed.AgentId, dto.RepositoryId, dto.RepositoryRoot, dto.MeasuredAtUtc, dto.TotalBytes, dto.FreeBytes, dto.RepositoryPhysicalBytes, dto.RestorePointCount, dto.LatestRestorePointUtc, dto.NewBytesLast7Days, dto.DailyGrowthBytes, dto.EstimatedDaysToFull, dto.Status, dto.Reason);
                state.RepositoryHealth[record.HealthId] = record;
            }
            catch (JsonException) { }
        }

        if (completed.Type != AgentCommandType.RestoreDrill) return;
        foreach (var run in state.RecoveryRuns.Values.Where(x => x.CompletedAtUtc is null).ToArray())
        {
            var index = run.Targets.ToList().FindIndex(x => x.CommandId == completed.CommandId);
            if (index < 0) continue;
            var targets = run.Targets.ToArray();
            if (completed.Succeeded)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<RestoreDrillResultDto>(resultJson);
                    targets[index] = targets[index] with { Status = "succeeded", Succeeded = true, VerifiedBytes = dto?.VerifiedBytes ?? 0, DurationMilliseconds = dto?.DurationMilliseconds ?? 0, Error = null };
                }
                catch (JsonException ex)
                {
                    targets[index] = targets[index] with { Status = "failed", Succeeded = false, Error = TrimError(ex.Message) };
                }
            }
            else
            {
                targets[index] = targets[index] with { Status = "failed", Succeeded = false, Error = TrimError(error) };
            }
            state.RecoveryRuns[run.RunId] = FinalizeRecoveryRunIfTerminal(run with { Targets = targets }, completedAt);
            break;
        }
        PruneResilienceStateUnsafe(state);
    }

    private static void PruneResilienceStateUnsafe(StateDocument state)
    {
        foreach (var id in state.RepositoryHealth.Values.OrderByDescending(x => x.MeasuredAtUtc).Skip(MaxRepositoryHealthRecords).Select(x => x.HealthId).ToArray()) state.RepositoryHealth.Remove(id);
        foreach (var id in state.RecoveryRuns.Values.OrderByDescending(x => x.StartedAtUtc).Skip(MaxRecoveryRuns).Select(x => x.RunId).ToArray()) state.RecoveryRuns.Remove(id);
        foreach (var id in state.NotificationDeliveries.Values.OrderByDescending(x => x.CreatedAtUtc).Skip(MaxNotificationDeliveries).Select(x => x.DeliveryId).ToArray()) state.NotificationDeliveries.Remove(id);
    }
}
