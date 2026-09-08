using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore : IControlPlaneStore, IResilienceStore, IProductionFabricStore, IBusinessContinuityStore, IDisasterRecoverySessionStore, IMeshCentralFleetStore, IDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletedCommandRetention = TimeSpan.FromDays(30);
    private const int MaxAttempts = 5;
    private const int MaxPendingCommandsPerAgent = 1000;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan ManagementLockoutDuration = TimeSpan.FromMinutes(15);
    private const int MaxAuditEvents = 50_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;
    private readonly DistributedStateCoordinator _distributedState;
    private readonly PostgreSqlStateEngine? _postgresql;
    private readonly bool _transactionalState;
    private readonly bool _directSqlReads;
    private readonly bool _directSqlMutations;
    private long _stateVersion;
    private StateDocument _state;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StateStore(IHostEnvironment environment)
    {
        var root = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR")
            ?? Path.Combine(environment.ContentRootPath, ".local");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "control-plane-state.json");
        _backupPath = _path + ".bak";
        _distributedState = new DistributedStateCoordinator(root);

        var engine = (Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_ENGINE") ?? "file").Trim().ToLowerInvariant();
        _transactionalState = engine == "postgresql";
        _directSqlReads = _transactionalState &&
            !string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_DIRECT_SQL_READS"), "false", StringComparison.OrdinalIgnoreCase);
        _directSqlMutations = _transactionalState &&
            !string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_DIRECT_SQL_MUTATIONS"), "false", StringComparison.OrdinalIgnoreCase);
        if (engine is not ("file" or "postgresql")) throw new InvalidOperationException("YAZMABACKUP_STATE_ENGINE must be file or postgresql.");

        if (_transactionalState)
        {
            var connectionString = Environment.GetEnvironmentVariable("YAZMABACKUP_POSTGRES_CONNECTION")
                ?? throw new InvalidOperationException("YAZMABACKUP_POSTGRES_CONNECTION is required when YAZMABACKUP_STATE_ENGINE=postgresql.");
            var clusterId = Environment.GetEnvironmentVariable("YAZMABACKUP_CLUSTER_ID") ?? "default";
            _postgresql = new PostgreSqlStateEngine(connectionString, clusterId);
            var bootstrap = Load();
            var bootstrapJson = JsonSerializer.Serialize(bootstrap, JsonOptions);
            var snapshot = _postgresql.LoadOrBootstrap(bootstrapJson);
            _state = JsonSerializer.Deserialize<StateDocument>(snapshot.Json, JsonOptions) ?? throw new InvalidDataException("PostgreSQL Control Plane state is empty.");
            _stateVersion = snapshot.Version;
        }
        else
        {
            _state = Load();
            _stateVersion = _distributedState.ReadVersion();
        }
    }

    public async Task<(AgentRecord Agent, string AccessToken)> EnrollAgentAsync(
        string enrollmentToken,
        string machineName,
        string operatingSystem,
        string agentVersion,
        IReadOnlyList<string> capabilities,
        string? keyExchangePublicKeyPem,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(enrollmentToken))
            throw new UnauthorizedAccessException("Enrollment token is required.");

        var enrollmentHash = HashToken(enrollmentToken);
        var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var grant = _state.EnrollmentGrants.Values.FirstOrDefault(g => FixedTimeEquals(g.TokenHash, enrollmentHash));
            if (grant is null || grant.ExpiresAtUtc <= now || grant.RemainingUses <= 0)
                throw new UnauthorizedAccessException("Enrollment token is invalid or expired.");

            var record = new AgentRecord(
                Guid.NewGuid(), machineName, operatingSystem, HashToken(accessToken), now, now, agentVersion,
                capabilities.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                keyExchangePublicKeyPem);

            var nextState = CloneState();
            nextState.Agents[record.AgentId] = record;
            if (grant.RemainingUses == 1)
                nextState.EnrollmentGrants.Remove(grant.GrantId);
            else
                nextState.EnrollmentGrants[grant.GrantId] = grant with { RemainingUses = grant.RemainingUses - 1 };

            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (record, accessToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<(EnrollmentGrant Grant, string EnrollmentToken)> CreateEnrollmentGrantAsync(TimeSpan validity, int maxUses, CancellationToken ct)
    {
        if (validity < TimeSpan.FromMinutes(1) || validity > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(validity), "Enrollment token validity must be between 1 minute and 24 hours.");
        if (maxUses is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(maxUses), "Enrollment token max uses must be between 1 and 5000.");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var grant = new EnrollmentGrant(Guid.NewGuid(), HashToken(token), now, now.Add(validity), maxUses);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            foreach (var expired in nextState.EnrollmentGrants.Values
                         .Where(g => g.ExpiresAtUtc <= now || g.RemainingUses <= 0)
                         .Select(g => g.GrantId)
                         .ToArray())
                nextState.EnrollmentGrants.Remove(expired);
            nextState.EnrollmentGrants[grant.GrantId] = grant;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (grant, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentRecord?> AuthenticateAsync(Guid agentId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var agent)) return null;
            return FixedTimeEquals(agent.TokenHash, HashToken(token)) ? agent : null;
        }
        finally { _gate.Release(); }
    }

    public async Task TouchAsync(Guid agentId, string machineName, string operatingSystem, string agentVersion, IReadOnlyList<string> capabilities, string? keyExchangePublicKeyPem, ProtectionTelemetryDto? protection, IReadOnlyList<RepositoryCircuitTelemetry>? repositoryCircuits, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var existing)) return;
            var nextState = CloneState();
            nextState.Agents[agentId] = existing with
            {
                MachineName = machineName,
                OperatingSystem = operatingSystem,
                AgentVersion = agentVersion,
                Capabilities = capabilities.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                KeyExchangePublicKeyPem = string.IsNullOrWhiteSpace(keyExchangePublicKeyPem) ? existing.KeyExchangePublicKeyPem : keyExchangePublicKeyPem,
                ProtectionStatus = protection?.Status ?? existing.ProtectionStatus,
                ProtectionReason = protection is null ? existing.ProtectionReason : protection.Reason,
                ProtectionTriggeredAtUtc = protection is null ? existing.ProtectionTriggeredAtUtc : protection.TriggeredAtUtc,
                ProtectionIncidentId = protection is null ? existing.ProtectionIncidentId : protection.IncidentId,
                RepositoryCircuits = repositoryCircuits is null ? existing.RepositoryCircuits : repositoryCircuits.Take(128).ToArray(),
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(CancellationToken ct)
    {
        if (_directSqlReads) return await _postgresql!.GetAgentsAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.Agents.Values.OrderBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<AgentRecord?> SetAgentAssignedUserAsync(Guid agentId, string? assignedUser, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(assignedUser) ? null : assignedUser.Trim();
        if (normalized is not null)
        {
            if (normalized.Length > 128 || normalized.Any(char.IsControl))
                throw new ArgumentException("Assigned user must be <= 128 characters and contain no control characters.", nameof(assignedUser));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var existing)) return null;
            var updated = existing with { AssignedUser = normalized };
            var nextState = CloneState();
            nextState.Agents[agentId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

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

    public async Task EnsureBootstrapAdministratorAsync(string username, string displayName, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.ManagementUsers.Count > 0) return;
            var normalized = NormalizeUsername(username);
            PasswordSecurity.ValidateNewPassword(password);
            var material = PasswordSecurity.HashPassword(password);
            var now = DateTimeOffset.UtcNow;
            var user = new ManagementUserRecord(
                Guid.NewGuid(), normalized, NormalizeDisplayName(displayName), material.HashBase64, material.SaltBase64,
                material.Iterations, [ManagementRoles.Administrator], true, true, now, null, 0, null);
            var nextState = CloneState();
            nextState.ManagementUsers[user.UserId] = user;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> AuthenticateManagementUserAsync(string username, string password, CancellationToken ct)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException)
        {
            PasswordSecurity.PerformDummyVerification(password);
            return null;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.ManagementUsers.Values.FirstOrDefault(u => string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                PasswordSecurity.PerformDummyVerification(password);
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (!current.Enabled || current.LockedUntilUtc is not null && current.LockedUntilUtc > now)
            {
                PasswordSecurity.PerformDummyVerification(password);
                return null;
            }

            if (!PasswordSecurity.Verify(password, current))
            {
                var failures = current.FailedLoginCount + 1;
                var lockedUntil = failures >= MaxFailedLogins ? now.Add(ManagementLockoutDuration) : current.LockedUntilUtc;
                if (failures >= MaxFailedLogins) failures = 0;
                var nextState = CloneState();
                nextState.ManagementUsers[current.UserId] = current with { FailedLoginCount = failures, LockedUntilUtc = lockedUntil };
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return null;
            }

            var authenticated = current with { LastLoginAtUtc = now, FailedLoginCount = 0, LockedUntilUtc = null };
            var successState = CloneState();
            successState.ManagementUsers[current.UserId] = authenticated;
            await CommitUnsafeAsync(successState, ct).ConfigureAwait(false);
            return authenticated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagementUserRecord>> GetManagementUsersAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ManagementUsers.Values.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord> CreateManagementUserAsync(string username, string displayName, string password, IReadOnlyList<string> roles, bool mustChangePassword, CancellationToken ct)
    {
        var normalized = NormalizeUsername(username);
        var normalizedRoles = NormalizeRoles(roles);
        PasswordSecurity.ValidateNewPassword(password);
        var material = PasswordSecurity.HashPassword(password);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.ManagementUsers.Values.Any(u => string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Management username already exists.");
            var now = DateTimeOffset.UtcNow;
            var user = new ManagementUserRecord(Guid.NewGuid(), normalized, NormalizeDisplayName(displayName), material.HashBase64, material.SaltBase64,
                material.Iterations, normalizedRoles, true, mustChangePassword, now, null, 0, null);
            var nextState = CloneState();
            nextState.ManagementUsers[user.UserId] = user;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return user;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> SetManagementUserEnabledAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current)) return null;
            if (!enabled && current.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
            {
                var enabledAdmins = _state.ManagementUsers.Values.Count(u => u.Enabled && u.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase));
                if (enabledAdmins <= 1) throw new InvalidOperationException("The last enabled administrator cannot be disabled.");
            }
            var updated = current with { Enabled = enabled, FailedLoginCount = 0, LockedUntilUtc = enabled ? null : current.LockedUntilUtc };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ChangeManagementPasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        PasswordSecurity.ValidateNewPassword(newPassword);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current) || !current.Enabled) return false;
            if (!PasswordSecurity.Verify(currentPassword, current)) return false;
            var material = PasswordSecurity.HashPassword(newPassword);
            var updated = current with
            {
                PasswordHashBase64 = material.HashBase64,
                PasswordSaltBase64 = material.SaltBase64,
                PasswordIterations = material.Iterations,
                MustChangePassword = false,
                FailedLoginCount = 0,
                LockedUntilUtc = null
            };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ResetManagementPasswordAfterBreakGlassAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        PasswordSecurity.ValidateNewPassword(newPassword);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current) || !current.Enabled || !current.MustChangePassword) return false;
            var material = PasswordSecurity.HashPassword(newPassword);
            var updated = current with
            {
                PasswordHashBase64 = material.HashBase64,
                PasswordSaltBase64 = material.SaltBase64,
                PasswordIterations = material.Iterations,
                MustChangePassword = false,
                FailedLoginCount = 0,
                LockedUntilUtc = null
            };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAuditEventAsync(AuditEventRecord auditEvent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            nextState.AuditEvents[auditEvent.EventId] = auditEvent;
            if (nextState.AuditEvents.Count > MaxAuditEvents)
            {
                foreach (var id in nextState.AuditEvents.Values.OrderBy(x => x.OccurredAtUtc).Take(nextState.AuditEvents.Count - MaxAuditEvents).Select(x => x.EventId).ToArray())
                    nextState.AuditEvents.Remove(id);
            }
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEventRecord>> GetAuditEventsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        if (_directSqlReads) return await _postgresql!.GetAuditEventsAsync(limit, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.AuditEvents.Values.OrderByDescending(a => a.OccurredAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }


    public async Task<(ManagementApiTokenRecord Record, string PlaintextToken)> CreateManagementApiTokenAsync(string name, IReadOnlyList<string> roles, TimeSpan validity, CancellationToken ct)
    {
        var normalizedName = NormalizeTokenName(name);
        var normalizedRoles = NormalizeRoles(roles);
        if (validity < TimeSpan.FromMinutes(5) || validity > TimeSpan.FromDays(90))
            throw new ArgumentOutOfRangeException(nameof(validity), "API token validity must be between 5 minutes and 90 days.");

        var plaintext = "ybmt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var record = new ManagementApiTokenRecord(Guid.NewGuid(), normalizedName, HashToken(plaintext), normalizedRoles, now, now.Add(validity), null, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            foreach (var expired in nextState.ManagementApiTokens.Values.Where(t => t.ExpiresAtUtc <= now || t.Revoked).Select(t => t.TokenId).ToArray())
                nextState.ManagementApiTokens.Remove(expired);
            nextState.ManagementApiTokens[record.TokenId] = record;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (record, plaintext);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementApiTokenRecord?> AuthenticateManagementApiTokenAsync(string plaintextToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken) || !plaintextToken.StartsWith("ybmt_", StringComparison.Ordinal) || plaintextToken.Length != 69)
            return null;
        var tokenHash = HashToken(plaintextToken);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.ManagementApiTokens.Values.FirstOrDefault(t => !t.Revoked && t.ExpiresAtUtc > now && FixedTimeEquals(t.TokenHash, tokenHash));
            if (current is null) return null;
            if (current.LastUsedAtUtc is null || now - current.LastUsedAtUtc > TimeSpan.FromMinutes(1))
            {
                var updated = current with { LastUsedAtUtc = now };
                var nextState = CloneState();
                nextState.ManagementApiTokens[current.TokenId] = updated;
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return updated;
            }
            return current;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagementApiTokenRecord>> GetManagementApiTokensAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ManagementApiTokens.Values.OrderByDescending(t => t.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeManagementApiTokenAsync(Guid tokenId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementApiTokens.TryGetValue(tokenId, out var current)) return false;
            if (current.Revoked) return true;
            var nextState = CloneState();
            nextState.ManagementApiTokens[tokenId] = current with { Revoked = true };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> CreateBreakGlassRecoveryCodesAsync(Guid administratorUserId, int count, TimeSpan validity, CancellationToken ct)
    {
        if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
        if (validity < TimeSpan.FromHours(1) || validity > TimeSpan.FromDays(90)) throw new ArgumentOutOfRangeException(nameof(validity));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(administratorUserId, out var admin) || !admin.Enabled || !admin.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Break-glass codes can only be issued for an enabled administrator.");

            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(validity);
            var plaintextCodes = new List<string>(count);
            var nextState = CloneState();
            foreach (var stale in nextState.BreakGlassRecoveryCodes.Values.Where(c => c.UserId == administratorUserId && c.UsedAtUtc is null).Select(c => c.CodeId).ToArray())
                nextState.BreakGlassRecoveryCodes.Remove(stale);
            for (var i = 0; i < count; i++)
            {
                var code = "YBRC-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                plaintextCodes.Add(code);
                var record = new BreakGlassRecoveryCodeRecord(Guid.NewGuid(), administratorUserId, HashToken(code), now, expires, null);
                nextState.BreakGlassRecoveryCodes[record.CodeId] = record;
            }
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return plaintextCodes;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> RedeemBreakGlassRecoveryCodeAsync(string username, string recoveryCode, CancellationToken ct)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrWhiteSpace(recoveryCode) || recoveryCode.Length > 64) return null;
        var codeHash = HashToken(recoveryCode.Trim().ToUpperInvariant());
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = _state.ManagementUsers.Values.FirstOrDefault(u => u.Enabled && string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase));
            if (user is null || !user.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase)) return null;
            var code = _state.BreakGlassRecoveryCodes.Values.FirstOrDefault(c => c.UserId == user.UserId && c.UsedAtUtc is null && c.ExpiresAtUtc > now && FixedTimeEquals(c.CodeHash, codeHash));
            if (code is null) return null;
            var nextState = CloneState();
            nextState.BreakGlassRecoveryCodes[code.CodeId] = code with { UsedAtUtc = now };
            var authenticated = user with { LastLoginAtUtc = now, FailedLoginCount = 0, LockedUntilUtc = null, MustChangePassword = true };
            nextState.ManagementUsers[user.UserId] = authenticated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return authenticated;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord> FindOrProvisionExternalUserAsync(string issuer, string subject, string username, string displayName, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var normalizedIssuer = NormalizeExternalIdentityPart(issuer, 512, nameof(issuer));
        var normalizedSubject = NormalizeExternalIdentityPart(subject, 256, nameof(subject));
        var normalizedRoles = NormalizeRoles(roles);
        if (normalizedRoles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("OIDC identities cannot be auto-provisioned with the local break-glass administrator role.");
        var proposedUsername = NormalizeUsername(username);
        var safeDisplayName = NormalizeDisplayName(displayName);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var identity = _state.ExternalIdentities.Values.FirstOrDefault(i => string.Equals(i.Issuer, normalizedIssuer, StringComparison.Ordinal) && string.Equals(i.Subject, normalizedSubject, StringComparison.Ordinal));
            if (identity is not null && _state.ManagementUsers.TryGetValue(identity.UserId, out var existing))
            {
                if (!existing.Enabled) throw new UnauthorizedAccessException("The externally mapped user is disabled.");
                var nextState = CloneState();
                nextState.ExternalIdentities[identity.IdentityId] = identity with { LastLoginAtUtc = now };
                var updated = existing with { LastLoginAtUtc = now, Roles = normalizedRoles, DisplayName = safeDisplayName };
                nextState.ManagementUsers[existing.UserId] = updated;
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return updated;
            }

            var finalUsername = proposedUsername;
            if (_state.ManagementUsers.Values.Any(u => string.Equals(u.Username, proposedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIssuer + "\n" + normalizedSubject))).ToLowerInvariant()[..12];
                finalUsername = "oidc-" + suffix;
            }
            var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            var material = PasswordSecurity.HashPassword(randomPassword);
            var user = new ManagementUserRecord(Guid.NewGuid(), finalUsername, safeDisplayName, material.HashBase64, material.SaltBase64, material.Iterations, normalizedRoles, true, false, now, now, 0, null);
            var link = new ExternalIdentityRecord(Guid.NewGuid(), user.UserId, normalizedIssuer, normalizedSubject, now, now);
            var createdState = CloneState();
            createdState.ManagementUsers[user.UserId] = user;
            createdState.ExternalIdentities[link.IdentityId] = link;
            await CommitUnsafeAsync(createdState, ct).ConfigureAwait(false);
            return user;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord> UpsertAlarmAsync(string fingerprint, string severity, string category, string title, string? details, Guid? agentId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        fingerprint = NormalizeAlarmText(fingerprint, 256, nameof(fingerprint));
        category = NormalizeAlarmText(category, 64, nameof(category));
        title = NormalizeAlarmText(title, 160, nameof(title));
        if (!AlarmSeverity.All.Contains(severity)) throw new ArgumentException("Alarm severity is invalid.", nameof(severity));
        details = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (details?.Length > 1024) details = details[..1024];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _state.Alarms.Values.FirstOrDefault(a => string.Equals(a.Fingerprint, fingerprint, StringComparison.Ordinal));
            var normalizedSeverity = severity.ToLowerInvariant();
            static DateTimeOffset DefaultDue(string sev, DateTimeOffset now) => sev == AlarmSeverity.Critical ? now.AddMinutes(15) : sev == AlarmSeverity.Warning ? now.AddHours(4) : now.AddHours(24);
            var record = existing is null
                ? new AlarmRecord(Guid.NewGuid(), fingerprint, normalizedSeverity, category, title, details, AlarmStatus.Open, agentId, nowUtc, nowUtc, null, null, null, null, null, DefaultDue(normalizedSeverity, nowUtc), nowUtc)
                : existing with { Severity = normalizedSeverity, Category = category, Title = title, Details = details, AgentId = agentId, LastSeenAtUtc = nowUtc, Status = existing.Status == AlarmStatus.Resolved ? AlarmStatus.Open : existing.Status, ResolvedAtUtc = null, DueAtUtc = existing.Status == AlarmStatus.Resolved ? DefaultDue(normalizedSeverity, nowUtc) : existing.DueAtUtc ?? DefaultDue(normalizedSeverity, nowUtc), WorkflowUpdatedAtUtc = existing.WorkflowUpdatedAtUtc ?? nowUtc };
            var nextState = CloneState();
            nextState.Alarms[record.AlarmId] = record;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ResolveAlarmAsync(string fingerprint, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.Alarms.Values.FirstOrDefault(a => string.Equals(a.Fingerprint, fingerprint, StringComparison.Ordinal) && a.Status != AlarmStatus.Resolved);
            if (current is null) return false;
            var nextState = CloneState();
            nextState.Alarms[current.AlarmId] = current with { Status = AlarmStatus.Resolved, ResolvedAtUtc = nowUtc, LastSeenAtUtc = nowUtc, WorkflowUpdatedAtUtc = nowUtc };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> AcknowledgeAlarmAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current) || current.Status == AlarmStatus.Resolved) return null;
            var updated = current with { Status = AlarmStatus.Acknowledged, AcknowledgedAtUtc = nowUtc, AcknowledgedBy = actor, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState();
            nextState.Alarms[alarmId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> UpdateAlarmWorkflowAsync(Guid alarmId, string? assignedTo, string? note, DateTimeOffset? dueAtUtc, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        assignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : NormalizeAlarmText(assignedTo, 128, nameof(assignedTo));
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 2000) note = note[..2000];
        if (dueAtUtc is not null && (dueAtUtc.Value < nowUtc.AddDays(-1) || dueAtUtc.Value > nowUtc.AddDays(365))) throw new ArgumentOutOfRangeException(nameof(dueAtUtc));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current)) return null;
            var updated = current with { AssignedTo = assignedTo, OperatorNote = note, DueAtUtc = dueAtUtc ?? current.DueAtUtc, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState(); nextState.Alarms[alarmId] = updated; await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> ResolveAlarmByIdAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current)) return null;
            var updated = current with { Status = AlarmStatus.Resolved, ResolvedAtUtc = nowUtc, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState(); nextState.Alarms[alarmId] = updated; await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(int limit, bool includeResolved, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 2000);
        if (_directSqlReads) return await _postgresql!.GetAlarmsAsync(limit, includeResolved, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _state.Alarms.Values.Where(a => includeResolved || a.Status != AlarmStatus.Resolved)
                .OrderByDescending(a => a.Status != AlarmStatus.Resolved)
                .ThenByDescending(a => a.Severity == AlarmSeverity.Critical)
                .ThenByDescending(a => a.LastSeenAtUtc).Take(limit).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ClusterLeaseRecord?> TryAcquireOrRenewClusterLeaseAsync(string leaseName, string ownerId, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken ct)
    {
        leaseName = NormalizeLeasePart(leaseName, 128, nameof(leaseName));
        ownerId = NormalizeLeasePart(ownerId, 128, nameof(ownerId));
        if (duration < TimeSpan.FromSeconds(10) || duration > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(duration));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.ClusterLeases.TryGetValue(leaseName, out var current);
            if (current is not null && current.ExpiresAtUtc > nowUtc && !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)) return null;
            var lease = current is not null && string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)
                ? current with { ExpiresAtUtc = nowUtc.Add(duration) }
                : new ClusterLeaseRecord(leaseName, ownerId, Guid.NewGuid(), (current?.Epoch ?? 0) + 1, nowUtc, nowUtc.Add(duration));
            var nextState = CloneState();
            nextState.ClusterLeases[leaseName] = lease;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return lease;
        }
        finally { _gate.Release(); }
    }

    public async Task<ClusterLeaseRecord?> GetClusterLeaseAsync(string leaseName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ClusterLeases.TryGetValue(leaseName, out var lease) ? lease : null; }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private StateDocument Load()
    {
        if (!File.Exists(_path)) return new StateDocument();
        try
        {
            return DeserializeState(_path);
        }
        catch (InvalidDataException) when (File.Exists(_backupPath))
        {
            return DeserializeState(_backupPath);
        }
    }

    private static StateDocument DeserializeState(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonOptions) ?? new StateDocument();
            return NormalizeLoadedState(loaded);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Control Plane state is corrupt: {path}", ex);
        }
    }

    private static StateDocument NormalizeLoadedState(StateDocument loaded)
    {
        if (!int.TryParse(loaded.SchemaVersion, out var schemaVersion) || schemaVersion < 1)
            throw new InvalidDataException($"Control Plane state schema is invalid: {loaded.SchemaVersion}");
        if (schemaVersion > 11)
            throw new UnsupportedStateSchemaException($"Control Plane state schema {schemaVersion} is newer than this binary supports. Downgrade is blocked.");

        var now = DateTimeOffset.UtcNow;
        var policies = new Dictionary<Guid, BackupPolicyRecord>(loaded.BackupPolicies.Count);
        foreach (var pair in loaded.BackupPolicies)
        {
            var policy = pair.Value;
            var drillDays = policy.RestoreDrillIntervalDays is >= 1 and <= 365 ? policy.RestoreDrillIntervalDays : 7;
            var nextDrill = policy.NextRestoreDrillAtUtc ?? now.AddDays(drillDays);
            var healthHours = policy.RepositoryHealthIntervalHours is >= 1 and <= 720 ? policy.RepositoryHealthIntervalHours : 24;
            var nextHealth = policy.NextRepositoryHealthAtUtc ?? now.AddHours(healthHours);
            policies[pair.Key] = policy with { RestoreDrillIntervalDays = drillDays, NextRestoreDrillAtUtc = nextDrill, RepositoryHealthIntervalHours = healthHours, NextRepositoryHealthAtUtc = nextHealth };
        }

        return new StateDocument
        {
            SchemaVersion = "11",
            Agents = new Dictionary<Guid, AgentRecord>(loaded.Agents),
            Commands = new Dictionary<Guid, AgentCommand>(loaded.Commands),
            EnrollmentGrants = new Dictionary<Guid, EnrollmentGrant>(loaded.EnrollmentGrants),
            BackupPolicies = policies,
            ManagementUsers = new Dictionary<Guid, ManagementUserRecord>(loaded.ManagementUsers),
            AuditEvents = new Dictionary<Guid, AuditEventRecord>(loaded.AuditEvents),
            ManagementApiTokens = new Dictionary<Guid, ManagementApiTokenRecord>(loaded.ManagementApiTokens),
            BreakGlassRecoveryCodes = new Dictionary<Guid, BreakGlassRecoveryCodeRecord>(loaded.BreakGlassRecoveryCodes),
            ExternalIdentities = new Dictionary<Guid, ExternalIdentityRecord>(loaded.ExternalIdentities),
            Alarms = new Dictionary<Guid, AlarmRecord>(loaded.Alarms),
            ClusterLeases = new Dictionary<string, ClusterLeaseRecord>(loaded.ClusterLeases, StringComparer.Ordinal),
            RepositoryHealth = new Dictionary<Guid, RepositoryHealthRecord>(loaded.RepositoryHealth),
            RecoveryPlans = new Dictionary<Guid, RecoveryPlanRecord>(loaded.RecoveryPlans),
            BusinessServiceDependencies = new Dictionary<Guid, BusinessServiceDependencyRecord>(loaded.BusinessServiceDependencies),
            DisasterRecoverySessions = new Dictionary<Guid, DisasterRecoverySessionRecord>(loaded.DisasterRecoverySessions),
            RecoveryRuns = new Dictionary<Guid, RecoveryRunRecord>(loaded.RecoveryRuns),
            MeshCentralLinks = new Dictionary<Guid, MeshCentralLinkRecord>(loaded.MeshCentralLinks),
            NotificationRoutes = new Dictionary<Guid, NotificationRouteRecord>(loaded.NotificationRoutes),
            NotificationDeliveries = new Dictionary<Guid, NotificationDeliveryRecord>(loaded.NotificationDeliveries),
            RecoveryRunbooks = new Dictionary<Guid, RecoveryRunbookRecord>(loaded.RecoveryRunbooks),
            RecoveryRunbookRuns = new Dictionary<Guid, RecoveryRunbookRunRecord>(loaded.RecoveryRunbookRuns),
            IntegrationCredentials = new Dictionary<Guid, IntegrationCredentialRecord>(loaded.IntegrationCredentials),
            MeshCentralSyncEvents = new Dictionary<Guid, MeshCentralSyncEventRecord>(loaded.MeshCentralSyncEvents),
            MeshCentralConnectors = new Dictionary<Guid, MeshCentralConnectorRecord>(loaded.MeshCentralConnectors),
            MeshCentralInventoryDevices = new Dictionary<string, MeshCentralInventoryDeviceRecord>(loaded.MeshCentralInventoryDevices, StringComparer.Ordinal),
            MeshCentralDeployments = new Dictionary<Guid, MeshCentralDeploymentRecord>(loaded.MeshCentralDeployments)
        };
    }


    public async Task<IReadOnlyList<BusinessServiceDependencyRecord>> GetDependenciesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.BusinessServiceDependencies.Values.OrderBy(x => x.ServiceId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DependsOnServiceId, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<BusinessServiceDependencyRecord> UpsertDependencyAsync(
        string serviceId, string dependsOnServiceId, string dependencyType, bool required, CancellationToken ct)
    {
        serviceId=(serviceId??string.Empty).Trim();
        dependsOnServiceId=(dependsOnServiceId??string.Empty).Trim();
        dependencyType=(dependencyType??string.Empty).Trim().ToLowerInvariant();
        if(serviceId.Length is <1 or >120 || dependsOnServiceId.Length is <1 or >120)
            throw new ArgumentException("Business service identifiers must be 1-120 characters.");
        if(string.Equals(serviceId,dependsOnServiceId,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A business service cannot depend on itself.");
        if(dependencyType is not ("hard" or "soft"))
            throw new ArgumentException("Dependency type must be hard or soft.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next=CloneState();
            var existing=next.BusinessServiceDependencies.Values.FirstOrDefault(x=>
                string.Equals(x.ServiceId,serviceId,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DependsOnServiceId,dependsOnServiceId,StringComparison.OrdinalIgnoreCase));
            var record=existing is null
                ? new BusinessServiceDependencyRecord(Guid.NewGuid(),serviceId,dependsOnServiceId,dependencyType,required,DateTimeOffset.UtcNow)
                : existing with { DependencyType=dependencyType, Required=required };
            next.BusinessServiceDependencies[record.DependencyId]=record;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteDependencyAsync(Guid dependencyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.BusinessServiceDependencies.ContainsKey(dependencyId)) return false;
            var next=CloneState();
            next.BusinessServiceDependencies.Remove(dependencyId);
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }


    public async Task<IReadOnlyList<DisasterRecoverySessionRecord>> GetDisasterRecoverySessionsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.DisasterRecoverySessions.Values.OrderByDescending(x=>x.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> CreateDisasterRecoverySessionAsync(
        string name, IReadOnlyList<DisasterRecoverySessionStepRecord> steps, CancellationToken ct)
    {
        name=(name??string.Empty).Trim();
        if(name.Length is <3 or >160) throw new ArgumentException("DR session name must be 3-160 characters.");
        if(steps.Count==0 || steps.Any(x=>x.Status=="blocked"))
            throw new InvalidOperationException("DR session cannot start from an empty or cyclic/blocked Recovery DAG.");
        var now=DateTimeOffset.UtcNow;
        var record=new DisasterRecoverySessionRecord(Guid.NewGuid(),name,"active",now,now,null,1,
            steps.Select(x=>x with { Status="pending", ApprovedAtUtc=null, VerifiedAtUtc=null, VerificationNote=null }).ToArray());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next=CloneState();
            next.DisasterRecoverySessions[record.SessionId]=record;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> ApproveDisasterRecoveryStepAsync(Guid sessionId,int order,CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status!="active") throw new InvalidOperationException("DR session is not active.");
            if(order!=session.CurrentOrder) throw new InvalidOperationException("Only the current dependency-safe DR step can be approved.");
            var step=session.Steps.FirstOrDefault(x=>x.Order==order) ?? throw new KeyNotFoundException("DR step not found.");
            if(step.Status=="verified") return session;
            var now=DateTimeOffset.UtcNow;
            var steps=session.Steps.Select(x=>x.Order==order?x with { Status="approved",ApprovedAtUtc=now }:x).ToArray();
            var updated=session with { Steps=steps };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> VerifyDisasterRecoveryStepAsync(Guid sessionId,int order,string verificationNote,CancellationToken ct)
    {
        verificationNote=(verificationNote??string.Empty).Trim();
        if(verificationNote.Length is <3 or >1000) throw new ArgumentException("Verification note must be 3-1000 characters.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status!="active" || order!=session.CurrentOrder) throw new InvalidOperationException("Only the current active DR step can be verified.");
            var step=session.Steps.FirstOrDefault(x=>x.Order==order) ?? throw new KeyNotFoundException("DR step not found.");
            if(step.Status!="approved") throw new InvalidOperationException("DR step requires explicit approval before verification.");
            var now=DateTimeOffset.UtcNow;
            var steps=session.Steps.Select(x=>x.Order==order?x with { Status="verified",VerifiedAtUtc=now,VerificationNote=verificationNote }:x).ToArray();
            var nextOrder=steps.Where(x=>x.Status!="verified").Select(x=>x.Order).DefaultIfEmpty(0).Min();
            var completed=nextOrder==0;
            var updated=session with { Steps=steps,CurrentOrder=nextOrder,Status=completed?"completed":"active",CompletedAtUtc=completed?now:null };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> CancelDisasterRecoverySessionAsync(Guid sessionId,CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status=="completed") return session;
            var updated=session with { Status="cancelled",CompletedAtUtc=DateTimeOffset.UtcNow };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    private StateDocument CloneState() => new()
    {
        SchemaVersion = "11",
        Agents = new Dictionary<Guid, AgentRecord>(_state.Agents),
        Commands = new Dictionary<Guid, AgentCommand>(_state.Commands),
        EnrollmentGrants = new Dictionary<Guid, EnrollmentGrant>(_state.EnrollmentGrants),
        BackupPolicies = new Dictionary<Guid, BackupPolicyRecord>(_state.BackupPolicies),
        ManagementUsers = new Dictionary<Guid, ManagementUserRecord>(_state.ManagementUsers),
        AuditEvents = new Dictionary<Guid, AuditEventRecord>(_state.AuditEvents),
        ManagementApiTokens = new Dictionary<Guid, ManagementApiTokenRecord>(_state.ManagementApiTokens),
        BreakGlassRecoveryCodes = new Dictionary<Guid, BreakGlassRecoveryCodeRecord>(_state.BreakGlassRecoveryCodes),
        ExternalIdentities = new Dictionary<Guid, ExternalIdentityRecord>(_state.ExternalIdentities),
        Alarms = new Dictionary<Guid, AlarmRecord>(_state.Alarms),
        ClusterLeases = new Dictionary<string, ClusterLeaseRecord>(_state.ClusterLeases, StringComparer.Ordinal),
        RepositoryHealth = new Dictionary<Guid, RepositoryHealthRecord>(_state.RepositoryHealth),
        RecoveryPlans = new Dictionary<Guid, RecoveryPlanRecord>(_state.RecoveryPlans),
        BusinessServiceDependencies = new Dictionary<Guid, BusinessServiceDependencyRecord>(_state.BusinessServiceDependencies),
        DisasterRecoverySessions = new Dictionary<Guid, DisasterRecoverySessionRecord>(_state.DisasterRecoverySessions),
        RecoveryRuns = new Dictionary<Guid, RecoveryRunRecord>(_state.RecoveryRuns),
        MeshCentralLinks = new Dictionary<Guid, MeshCentralLinkRecord>(_state.MeshCentralLinks),
        NotificationRoutes = new Dictionary<Guid, NotificationRouteRecord>(_state.NotificationRoutes),
        NotificationDeliveries = new Dictionary<Guid, NotificationDeliveryRecord>(_state.NotificationDeliveries),
        RecoveryRunbooks = new Dictionary<Guid, RecoveryRunbookRecord>(_state.RecoveryRunbooks),
        RecoveryRunbookRuns = new Dictionary<Guid, RecoveryRunbookRunRecord>(_state.RecoveryRunbookRuns),
        IntegrationCredentials = new Dictionary<Guid, IntegrationCredentialRecord>(_state.IntegrationCredentials),
        MeshCentralSyncEvents = new Dictionary<Guid, MeshCentralSyncEventRecord>(_state.MeshCentralSyncEvents),
        MeshCentralConnectors = new Dictionary<Guid, MeshCentralConnectorRecord>(_state.MeshCentralConnectors),
        MeshCentralInventoryDevices = new Dictionary<string, MeshCentralInventoryDeviceRecord>(_state.MeshCentralInventoryDevices, StringComparer.Ordinal),
        MeshCentralDeployments = new Dictionary<Guid, MeshCentralDeploymentRecord>(_state.MeshCentralDeployments)
    };


    private async Task CommitMultiAggregateCompletionUnsafeAsync(
        StateDocument nextState,
        AgentCommand completedCommand,
        Guid completionLeaseId,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(nextState, JsonOptions);
        var committed = await _postgresql!.TryCommitMultiAggregateCompletionAsync(
            _stateVersion, json, completedCommand, completionLeaseId, ct).ConfigureAwait(false);
        await AcceptFocusedCommitUnsafeAsync(nextState, committed, ct).ConfigureAwait(false);
    }

    private void AcceptDatabaseSnapshotUnsafe(TransactionalStateSnapshot snapshot)
    {
        _state = JsonSerializer.Deserialize<StateDocument>(snapshot.Json, JsonOptions)
            ?? throw new InvalidDataException("PostgreSQL Control Plane state is empty.");
        _stateVersion = snapshot.Version;
    }

    private async Task CommitCommandMutationUnsafeAsync(StateDocument nextState, AgentCommand command, CancellationToken ct)
    {
        if (!_directSqlMutations)
        {
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(nextState, JsonOptions);
        var committed = await _postgresql!.TryCommitCommandMutationAsync(_stateVersion, json, command, ct).ConfigureAwait(false);
        await AcceptFocusedCommitUnsafeAsync(nextState, committed, ct).ConfigureAwait(false);
    }

    private async Task CommitPolicyMutationUnsafeAsync(StateDocument nextState, BackupPolicyRecord policy, CancellationToken ct)
    {
        if (!_directSqlMutations)
        {
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(nextState, JsonOptions);
        var committed = await _postgresql!.TryCommitPolicyMutationAsync(_stateVersion, json, policy, ct).ConfigureAwait(false);
        await AcceptFocusedCommitUnsafeAsync(nextState, committed, ct).ConfigureAwait(false);
    }

    private async Task CommitPolicyDeleteUnsafeAsync(StateDocument nextState, Guid policyId, CancellationToken ct)
    {
        if (!_directSqlMutations)
        {
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(nextState, JsonOptions);
        var committed = await _postgresql!.TryCommitPolicyDeleteAsync(_stateVersion, json, policyId, ct).ConfigureAwait(false);
        await AcceptFocusedCommitUnsafeAsync(nextState, committed, ct).ConfigureAwait(false);
    }

    private async Task AcceptFocusedCommitUnsafeAsync(
        StateDocument nextState,
        TransactionalStateSnapshot? committed,
        CancellationToken ct)
    {
        if (committed is null)
        {
            var expectedVersion = _stateVersion;
            var latest = await _postgresql!.LoadAsync(ct).ConfigureAwait(false);
            _state = JsonSerializer.Deserialize<StateDocument>(latest.Json, JsonOptions)
                ?? throw new InvalidDataException("PostgreSQL Control Plane state is empty.");
            _stateVersion = latest.Version;
            throw new DistributedStateConflictException(
                $"Transactional Control Plane state changed on another node. Expected version {expectedVersion}, observed {latest.Version}; operation must be retried.");
        }

        _state = nextState;
        _stateVersion = committed.Version;
    }

    private async Task CommitUnsafeAsync(StateDocument nextState, CancellationToken ct)
    {
        if (_transactionalState)
        {
            var json = JsonSerializer.Serialize(nextState, JsonOptions);
            var committed = await _postgresql!.TryCommitAsync(_stateVersion, json, ct).ConfigureAwait(false);
            if (committed is null)
            {
                var expectedVersion = _stateVersion;
                var latest = await _postgresql.LoadAsync(ct).ConfigureAwait(false);
                _state = JsonSerializer.Deserialize<StateDocument>(latest.Json, JsonOptions) ?? throw new InvalidDataException("PostgreSQL Control Plane state is empty.");
                _stateVersion = latest.Version;
                throw new DistributedStateConflictException($"Transactional Control Plane state changed on another node. Expected version {expectedVersion}, observed {latest.Version}; operation must be retried.");
            }

            _state = nextState;
            _stateVersion = committed.Version;
            return;
        }

        await using var distributedLease = await _distributedState.AcquireWriteLeaseAsync(ct).ConfigureAwait(false);
        var diskVersion = _distributedState.ReadVersion();
        if (diskVersion != _stateVersion)
        {
            var expectedVersion = _stateVersion;
            _state = Load();
            _stateVersion = diskVersion;
            throw new DistributedStateConflictException($"Control Plane state changed on another node. Expected version {expectedVersion}, observed {diskVersion}; operation must be retried.");
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, nextState, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Copy(_path, _backupPath, overwrite: true);
            File.Move(temp, _path, overwrite: true);
            var nextVersion = checked(diskVersion + 1);
            _distributedState.WriteVersion(nextVersion);
            _state = nextState;
            _stateVersion = nextVersion;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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

    private static bool IsLeaseExpiredOrMissing(AgentCommand command, DateTimeOffset now) =>
        command.LeaseId is null || command.LeaseExpiresAtUtc is null || command.LeaseExpiresAtUtc <= now;

    private static string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim();
        if (normalized.Length > 128) throw new ArgumentException("Idempotency key cannot exceed 128 characters.", nameof(key));
        return normalized;
    }

    private static string NormalizeUsername(string username)
    {
        var value = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 3 or > 64 || !value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Management username is invalid.", nameof(username));
        return value;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        var value = (displayName ?? string.Empty).Trim();
        if (value.Length is < 2 or > 128) throw new ArgumentException("Display name is invalid.", nameof(displayName));
        return value;
    }

    private static string[] NormalizeRoles(IReadOnlyList<string> roles)
    {
        var normalized = (roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Any(r => !ManagementRoles.All.Contains(r)))
            throw new ArgumentException("At least one valid management role is required.", nameof(roles));
        return normalized;
    }


    private static string NormalizeTokenName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length is < 3 or > 64 || value.Any(char.IsControl)) throw new ArgumentException("API token name is invalid.", nameof(name));
        return value;
    }

    private static string NormalizeExternalIdentityPart(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || normalized.Any(char.IsControl)) throw new ArgumentException("External identity value is invalid.", parameterName);
        return normalized;
    }

    private static string NormalizeAlarmText(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || normalized.Any(char.IsControl)) throw new ArgumentException("Alarm value is invalid.", parameterName);
        return normalized;
    }

    private static string NormalizeLeasePart(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || !normalized.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) throw new ArgumentException("Cluster lease value is invalid.", parameterName);
        return normalized;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static bool FixedTimeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private sealed class UnsupportedStateSchemaException(string message) : Exception(message);

    public sealed partial class StateDocument
    {
        public string SchemaVersion { get; init; } = "11";
        public Dictionary<Guid, AgentRecord> Agents { get; init; } = [];
        public Dictionary<Guid, AgentCommand> Commands { get; init; } = [];
        public Dictionary<Guid, EnrollmentGrant> EnrollmentGrants { get; init; } = [];
        public Dictionary<Guid, BackupPolicyRecord> BackupPolicies { get; init; } = [];
        public Dictionary<Guid, ManagementUserRecord> ManagementUsers { get; init; } = [];
        public Dictionary<Guid, AuditEventRecord> AuditEvents { get; init; } = [];
        public Dictionary<Guid, ManagementApiTokenRecord> ManagementApiTokens { get; init; } = [];
        public Dictionary<Guid, BreakGlassRecoveryCodeRecord> BreakGlassRecoveryCodes { get; init; } = [];
        public Dictionary<Guid, ExternalIdentityRecord> ExternalIdentities { get; init; } = [];
        public Dictionary<Guid, AlarmRecord> Alarms { get; init; } = [];
        public Dictionary<string, ClusterLeaseRecord> ClusterLeases { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, RepositoryHealthRecord> RepositoryHealth { get; init; } = [];
        public Dictionary<Guid, RecoveryPlanRecord> RecoveryPlans { get; init; } = [];
        public Dictionary<Guid, BusinessServiceDependencyRecord> BusinessServiceDependencies { get; init; } = [];
        public Dictionary<Guid, DisasterRecoverySessionRecord> DisasterRecoverySessions { get; init; } = [];
        public Dictionary<Guid, RecoveryRunRecord> RecoveryRuns { get; init; } = [];
        public Dictionary<Guid, MeshCentralLinkRecord> MeshCentralLinks { get; init; } = [];
        public Dictionary<Guid, NotificationRouteRecord> NotificationRoutes { get; init; } = [];
        public Dictionary<Guid, NotificationDeliveryRecord> NotificationDeliveries { get; init; } = [];
        public Dictionary<Guid, RecoveryRunbookRecord> RecoveryRunbooks { get; init; } = [];
        public Dictionary<Guid, RecoveryRunbookRunRecord> RecoveryRunbookRuns { get; init; } = [];
        public Dictionary<Guid, IntegrationCredentialRecord> IntegrationCredentials { get; init; } = [];
        public Dictionary<Guid, MeshCentralSyncEventRecord> MeshCentralSyncEvents { get; init; } = [];
        public Dictionary<Guid, MeshCentralConnectorRecord> MeshCentralConnectors { get; init; } = [];
        public Dictionary<string, MeshCentralInventoryDeviceRecord> MeshCentralInventoryDevices { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, MeshCentralDeploymentRecord> MeshCentralDeployments { get; init; } = [];
    }
}
