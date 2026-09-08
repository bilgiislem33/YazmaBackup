using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<BackupPolicyRecord> CreateBackupPolicyAsync(BackupPolicyRecord policy, CancellationToken ct)
    {
        ValidatePolicy(policy);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.ContainsKey(policy.AgentId)) throw new KeyNotFoundException("Agent not found.");
            if (_state.BackupPolicies.ContainsKey(policy.PolicyId)) throw new InvalidOperationException("Backup policy already exists.");
            var nextState = CloneState();
            nextState.BackupPolicies[policy.PolicyId] = policy;
            await CommitPolicyMutationUnsafeAsync(nextState, policy, ct).ConfigureAwait(false);
            return policy;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BackupPolicyRecord>> CreateBackupPoliciesAsync(IReadOnlyList<BackupPolicyRecord> policies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(policies), "Bulk policy count must be 1..1000.");
        foreach (var policy in policies) ValidatePolicy(policy);
        if (policies.Select(x => x.PolicyId).Distinct().Count() != policies.Count)
            throw new ArgumentException("Bulk policies contain duplicate policy identifiers.", nameof(policies));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var policy in policies)
            {
                if (!_state.Agents.ContainsKey(policy.AgentId)) throw new KeyNotFoundException($"Agent not found: {policy.AgentId:D}.");
                if (_state.BackupPolicies.ContainsKey(policy.PolicyId)) throw new InvalidOperationException($"Backup policy already exists: {policy.PolicyId:D}.");
            }

            var nextState = CloneState();
            foreach (var policy in policies) nextState.BackupPolicies[policy.PolicyId] = policy;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return policies.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BackupPolicyRecord>> GetBackupPoliciesAsync(CancellationToken ct)
    {
        if (_directSqlReads) return await _postgresql!.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.BackupPolicies.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<BackupPolicyRecord?> SetBackupPolicyEnabledAsync(Guid policyId, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.BackupPolicies.TryGetValue(policyId, out var current)) return null;
            var next = current with { Enabled = enabled, NextRunAtUtc = enabled && !current.Enabled ? DateTimeOffset.UtcNow : current.NextRunAtUtc };
            var nextState = CloneState();
            nextState.BackupPolicies[policyId] = next;
            await CommitPolicyMutationUnsafeAsync(nextState, next, ct).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteBackupPolicyAsync(Guid policyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.BackupPolicies.ContainsKey(policyId)) return false;
            var nextState = CloneState();
            nextState.BackupPolicies.Remove(policyId);
            await CommitPolicyDeleteUnsafeAsync(nextState, policyId, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> EnqueueDueBackupPoliciesAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            var changed = PruneCompletedCommands(nextState, nowUtc.Subtract(CompletedCommandRetention));
            var enqueued = 0;

            foreach (var original in nextState.BackupPolicies.Values.Where(p => p.Enabled).OrderBy(p => p.NextRunAtUtc).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var policy = original;

                if (policy.NextRestoreDrillAtUtc is null)
                {
                    policy = policy with { NextRestoreDrillAtUtc = nowUtc.AddDays(policy.RestoreDrillIntervalDays) };
                    nextState.BackupPolicies[policy.PolicyId] = policy;
                    changed = true;
                }

                if (policy.NextRunAtUtc <= nowUtc && PendingCount(nextState, policy.AgentId) < MaxPendingCommandsPerAgent)
                {
                    var scheduledFor = policy.NextRunAtUtc;
                    var idempotencyKey = $"policy:{policy.PolicyId:N}:backup:{scheduledFor.UtcDateTime.Ticks}";
                    var payload = new BackupPayload(
                        policy.SourcePath, policy.RepositoryRoot, policy.RepositoryId, policy.RequireSnapshot,
                        policy.ActiveBytesPerSecond, policy.IdleBytesPerSecond, policy.UserIdleThresholdSeconds,
                        policy.Retention, policy.Protection ?? new ProtectionPolicy());
                    var command = NewCommand(policy.AgentId, AgentCommandType.BackupPath, JsonSerializer.Serialize(payload), idempotencyKey, nowUtc);
                    nextState.Commands[command.CommandId] = command;

                    var nextRun = scheduledFor;
                    do { nextRun = nextRun.AddMinutes(policy.IntervalMinutes); } while (nextRun <= nowUtc);
                    policy = policy with { LastScheduledAtUtc = nowUtc, NextRunAtUtc = nextRun };
                    nextState.BackupPolicies[policy.PolicyId] = policy;
                    enqueued++;
                    changed = true;
                }

                if (policy.NextRestoreDrillAtUtc is { } nextDrill && nextDrill <= nowUtc && PendingCount(nextState, policy.AgentId) < MaxPendingCommandsPerAgent)
                {
                    var idempotencyKey = $"policy:{policy.PolicyId:N}:restore-drill:{nextDrill.UtcDateTime.Ticks}";
                    var drillPayload = new RestoreDrillPayload(policy.RepositoryRoot, policy.RepositoryId, policy.SourcePath);
                    var command = NewCommand(policy.AgentId, AgentCommandType.RestoreDrill, JsonSerializer.Serialize(drillPayload), idempotencyKey, nowUtc);
                    nextState.Commands[command.CommandId] = command;

                    var followingDrill = nextDrill;
                    do { followingDrill = followingDrill.AddDays(policy.RestoreDrillIntervalDays); } while (followingDrill <= nowUtc);
                    policy = policy with { LastRestoreDrillScheduledAtUtc = nowUtc, NextRestoreDrillAtUtc = followingDrill };
                    nextState.BackupPolicies[policy.PolicyId] = policy;
                    enqueued++;
                    changed = true;
                }
            }

            if (changed) await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return enqueued;
        }
        finally { _gate.Release(); }
    }

    private static void ValidatePolicy(BackupPolicyRecord policy)
    {
        if (policy.PolicyId == Guid.Empty) throw new ArgumentException("PolicyId is required.", nameof(policy));
        if (string.IsNullOrWhiteSpace(policy.Name) || policy.Name.Length > 128) throw new ArgumentException("Policy name is invalid.", nameof(policy));
        if (string.IsNullOrWhiteSpace(policy.SourcePath) || policy.SourcePath.Length > 32767) throw new ArgumentException("Source path is invalid.", nameof(policy));
        if (string.IsNullOrWhiteSpace(policy.RepositoryRoot) || policy.RepositoryRoot.Length > 32767) throw new ArgumentException("Repository root is invalid.", nameof(policy));
        if (!ValidIdentifier(policy.RepositoryId)) throw new ArgumentException("Repository id is invalid.", nameof(policy));
        if (policy.IntervalMinutes is < 5 or > 43200) throw new ArgumentOutOfRangeException(nameof(policy), "IntervalMinutes must be 5..43200.");
        if (policy.ActiveBytesPerSecond is < 0 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.IdleBytesPerSecond is < 0 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.UserIdleThresholdSeconds is < 30 or > 86400) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.RestoreDrillIntervalDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(policy), "RestoreDrillIntervalDays must be 1..365.");
        if (policy.RepositoryHealthIntervalHours is < 1 or > 720) throw new ArgumentOutOfRangeException(nameof(policy), "RepositoryHealthIntervalHours must be 1..720.");
        policy.Retention.Validate();
        (policy.Protection ?? new ProtectionPolicy()).Validate();
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
