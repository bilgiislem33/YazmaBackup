using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private static readonly TimeSpan CompletedCommandRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private const int MaxAttempts = 5;

    private const int MaxPendingCommandsPerAgent = 1000;

    public async Task<AgentCommand> EnqueueAsync(Guid agentId, AgentCommandType type, string payloadJson, string? idempotencyKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.ContainsKey(agentId)) throw new KeyNotFoundException("Agent not found.");
            var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
            if (normalizedKey is not null)
            {
                var existing = _state.Commands.Values
                    .Where(c => c.AgentId == agentId && c.Type == type && string.Equals(c.IdempotencyKey, normalizedKey, StringComparison.Ordinal))
                    .OrderByDescending(c => c.CreatedAtUtc)
                    .FirstOrDefault();
                if (existing is not null) return existing;
            }
            EnsureQueueCapacity(_state, agentId);
            var command = NewCommand(agentId, type, payloadJson, normalizedKey, DateTimeOffset.UtcNow);
            var nextState = CloneState();
            nextState.Commands[command.CommandId] = command;
            await CommitCommandMutationUnsafeAsync(nextState, command, ct).ConfigureAwait(false);
            return command;
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentCommand?> ClaimNextAsync(Guid agentId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_directSqlMutations)
            {
                var result = await _postgresql!.ClaimNextCommandAsync(
                    agentId, MaxAttempts, LeaseDuration, CompletedCommandRetention, ct).ConfigureAwait(false);
                AcceptDatabaseSnapshotUnsafe(result.Snapshot);
                return result.Command;
            }

            var now = DateTimeOffset.UtcNow;
            var nextState = CloneState();
            var changed = PruneCompletedCommands(nextState, now.Subtract(CompletedCommandRetention));

            foreach (var exhausted in nextState.Commands.Values
                         .Where(c => c.AgentId == agentId && c.CompletedAtUtc is null && c.AttemptCount >= MaxAttempts && IsLeaseExpiredOrMissing(c, now))
                         .ToArray())
            {
                nextState.Commands[exhausted.CommandId] = exhausted with
                {
                    CompletedAtUtc = now,
                    Succeeded = false,
                    Error = $"Command exhausted after {MaxAttempts} delivery attempts.",
                    LeaseId = null,
                    LeaseExpiresAtUtc = null
                };
                changed = true;
            }

            var next = nextState.Commands.Values
                .Where(c => c.AgentId == agentId && c.CompletedAtUtc is null && c.AttemptCount < MaxAttempts && IsLeaseExpiredOrMissing(c, now))
                .OrderBy(c => c.CreatedAtUtc)
                .FirstOrDefault();

            if (next is null)
            {
                if (changed) await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return null;
            }

            var leaseId = Guid.NewGuid();
            var claimed = next with
            {
                ClaimedAtUtc = now,
                LeaseId = leaseId,
                LeaseExpiresAtUtc = now.Add(LeaseDuration),
                LastLeaseRenewalUtc = now,
                AttemptCount = next.AttemptCount + 1
            };
            nextState.Commands[claimed.CommandId] = claimed;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return claimed;
        }
        finally { _gate.Release(); }
    }

    public async Task<DateTimeOffset> RenewLeaseAsync(Guid agentId, Guid commandId, Guid leaseId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_directSqlMutations)
            {
                var result = await _postgresql!.RenewCommandLeaseAsync(
                    agentId, commandId, leaseId, LeaseDuration, ct).ConfigureAwait(false);
                AcceptDatabaseSnapshotUnsafe(result.Snapshot);
                return result.ExpiresAtUtc;
            }

            if (!_state.Commands.TryGetValue(commandId, out var command) || command.AgentId != agentId)
                throw new KeyNotFoundException("Command not found.");
            if (command.CompletedAtUtc is not null)
                throw new InvalidOperationException("Command is already completed.");
            if (command.LeaseId != leaseId)
                throw new InvalidOperationException("Command lease is no longer owned by this execution.");
            var now = DateTimeOffset.UtcNow;
            if (command.LeaseExpiresAtUtc is null || command.LeaseExpiresAtUtc <= now)
                throw new InvalidOperationException("Command lease has expired.");

            var expires = now.Add(LeaseDuration);
            var nextState = CloneState();
            nextState.Commands[commandId] = command with { LastLeaseRenewalUtc = now, LeaseExpiresAtUtc = expires };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return expires;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(Guid agentId, Guid commandId, Guid leaseId, bool succeeded, string resultJson, string? errorMessage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Commands.TryGetValue(commandId, out var command) || command.AgentId != agentId)
                throw new KeyNotFoundException("Command not found.");
            if (command.CompletedAtUtc is not null) return;
            if (command.LeaseId != leaseId)
                throw new InvalidOperationException("Command lease is no longer owned by this execution.");
            if (command.LeaseExpiresAtUtc is null || command.LeaseExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Command lease expired before result commit.");

            var nextState = CloneState();
            var completedAt = DateTimeOffset.UtcNow;
            var completed = command with
            {
                CompletedAtUtc = completedAt,
                Succeeded = succeeded,
                ResultJson = resultJson,
                Error = errorMessage,
                LeaseId = null,
                LeaseExpiresAtUtc = null
            };
            nextState.Commands[commandId] = completed;
            ApplyResilienceCommandResultUnsafe(nextState, completed, resultJson, errorMessage, completedAt);
            if (_directSqlMutations)
                await CommitMultiAggregateCompletionUnsafeAsync(nextState, completed, leaseId, ct).ConfigureAwait(false);
            else
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentCommand?> GetCommandAsync(Guid commandId, CancellationToken ct)
    {
        if (_directSqlReads) return await _postgresql!.GetCommandAsync(commandId, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.Commands.GetValueOrDefault(commandId); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentCommand>> GetRecentCommandsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        if (_directSqlReads) return await _postgresql!.GetRecentCommandsAsync(limit, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _state.Commands.Values
                .OrderByDescending(c => c.CreatedAtUtc)
                .Take(limit)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationalCommandMetrics> GetOperationalCommandMetricsAsync(DateTimeOffset backupSinceUtc, DateTimeOffset restoreDrillSinceUtc, CancellationToken ct)
    {
        if (_directSqlReads) return await _postgresql!.GetOperationalCommandMetricsAsync(backupSinceUtc, restoreDrillSinceUtc, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var commands = _state.Commands.Values;
            return new OperationalCommandMetrics(
                commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc >= backupSinceUtc && c.Succeeded),
                commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc >= backupSinceUtc && !c.Succeeded),
                commands.Count(c => c.Type == AgentCommandType.RestoreDrill && c.CompletedAtUtc >= restoreDrillSinceUtc && c.Succeeded),
                commands.Count(c => c.Type == AgentCommandType.RestoreDrill && c.CompletedAtUtc >= restoreDrillSinceUtc && !c.Succeeded));
        }
        finally { _gate.Release(); }
    }

    private static AgentCommand NewCommand(Guid agentId, AgentCommandType type, string payloadJson, string? idempotencyKey, DateTimeOffset now) =>
        new(Guid.NewGuid(), agentId, type, payloadJson, now, null, null, null, false, null, null, null, null, 0, idempotencyKey);

    private static void EnsureQueueCapacity(StateDocument state, Guid agentId)
    {
        if (PendingCount(state, agentId) >= MaxPendingCommandsPerAgent)
            throw new InvalidOperationException($"Agent pending command limit ({MaxPendingCommandsPerAgent}) reached.");
    }

    private static int PendingCount(StateDocument state, Guid agentId) =>
        state.Commands.Values.Count(c => c.AgentId == agentId && c.CompletedAtUtc is null);

    private static bool PruneCompletedCommands(StateDocument state, DateTimeOffset cutoffUtc)
    {
        var ids = state.Commands.Values.Where(c => c.CompletedAtUtc is not null && c.CompletedAtUtc < cutoffUtc).Select(c => c.CommandId).ToArray();
        foreach (var id in ids) state.Commands.Remove(id);
        return ids.Length > 0;
    }

    private static bool IsLeaseExpiredOrMissing(AgentCommand command, DateTimeOffset now) =>
        command.LeaseId is null || command.LeaseExpiresAtUtc is null || command.LeaseExpiresAtUtc <= now;

    private static string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim();
        if (normalized.Length > 128) throw new ArgumentException("Idempotency key cannot exceed 128 characters.", nameof(key));
        return normalized;
    }
}
