using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
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
            var loaded = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Control Plane state is null: {path}");
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

    private sealed class UnsupportedStateSchemaException(string message) : Exception(message);
}
