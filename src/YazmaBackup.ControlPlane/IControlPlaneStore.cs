using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IControlPlaneStore
{
    Task<(AgentRecord Agent, string AccessToken)> EnrollAgentAsync(string enrollmentToken, string machineName, string operatingSystem, string agentVersion, IReadOnlyList<string> capabilities, string? keyExchangePublicKeyPem, CancellationToken ct);
    Task<(EnrollmentGrant Grant, string EnrollmentToken)> CreateEnrollmentGrantAsync(TimeSpan validity, int maxUses, CancellationToken ct);
    Task<AgentRecord?> AuthenticateAsync(Guid agentId, string? token, CancellationToken ct);
    Task TouchAsync(Guid agentId, string machineName, string operatingSystem, string agentVersion, IReadOnlyList<string> capabilities, string? keyExchangePublicKeyPem, ProtectionTelemetryDto? protection, IReadOnlyList<RepositoryCircuitTelemetry>? repositoryCircuits, CancellationToken ct);
    Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(CancellationToken ct);
    Task<AgentRecord?> SetAgentAssignedUserAsync(Guid agentId, string? assignedUser, CancellationToken ct);
    Task<AgentCommand> EnqueueAsync(Guid agentId, AgentCommandType type, string payloadJson, string? idempotencyKey, CancellationToken ct);
    Task<AgentCommand?> ClaimNextAsync(Guid agentId, CancellationToken ct);
    Task<DateTimeOffset> RenewLeaseAsync(Guid agentId, Guid commandId, Guid leaseId, CancellationToken ct);
    Task CompleteAsync(Guid agentId, Guid commandId, Guid leaseId, bool succeeded, string resultJson, string? errorMessage, CancellationToken ct);
    Task<AgentCommand?> GetCommandAsync(Guid commandId, CancellationToken ct);
    Task<IReadOnlyList<AgentCommand>> GetRecentCommandsAsync(int limit, CancellationToken ct);
    Task<OperationalCommandMetrics> GetOperationalCommandMetricsAsync(DateTimeOffset backupSinceUtc, DateTimeOffset restoreDrillSinceUtc, CancellationToken ct);
    Task<BackupPolicyRecord> CreateBackupPolicyAsync(BackupPolicyRecord policy, CancellationToken ct);
    Task<IReadOnlyList<BackupPolicyRecord>> CreateBackupPoliciesAsync(IReadOnlyList<BackupPolicyRecord> policies, CancellationToken ct);
    Task<IReadOnlyList<BackupPolicyRecord>> GetBackupPoliciesAsync(CancellationToken ct);
    Task<BackupPolicyRecord?> SetBackupPolicyEnabledAsync(Guid policyId, bool enabled, CancellationToken ct);
    Task<bool> DeleteBackupPolicyAsync(Guid policyId, CancellationToken ct);
    Task<int> EnqueueDueBackupPoliciesAsync(DateTimeOffset nowUtc, CancellationToken ct);

    Task EnsureBootstrapAdministratorAsync(string username, string displayName, string password, CancellationToken ct);
    Task<ManagementUserRecord?> AuthenticateManagementUserAsync(string username, string password, CancellationToken ct);
    Task<IReadOnlyList<ManagementUserRecord>> GetManagementUsersAsync(CancellationToken ct);
    Task<ManagementUserRecord> CreateManagementUserAsync(string username, string displayName, string password, IReadOnlyList<string> roles, bool mustChangePassword, CancellationToken ct);
    Task<ManagementUserRecord?> SetManagementUserEnabledAsync(Guid userId, bool enabled, CancellationToken ct);
    Task<bool> ChangeManagementPasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct);
    Task<bool> ResetManagementPasswordAfterBreakGlassAsync(Guid userId, string newPassword, CancellationToken ct);
    Task AppendAuditEventAsync(AuditEventRecord auditEvent, CancellationToken ct);
    Task<IReadOnlyList<AuditEventRecord>> GetAuditEventsAsync(int limit, CancellationToken ct);

    Task<(ManagementApiTokenRecord Record, string PlaintextToken)> CreateManagementApiTokenAsync(string name, IReadOnlyList<string> roles, TimeSpan validity, CancellationToken ct);
    Task<ManagementApiTokenRecord?> AuthenticateManagementApiTokenAsync(string plaintextToken, CancellationToken ct);
    Task<IReadOnlyList<ManagementApiTokenRecord>> GetManagementApiTokensAsync(CancellationToken ct);
    Task<bool> RevokeManagementApiTokenAsync(Guid tokenId, CancellationToken ct);
    Task<IReadOnlyList<string>> CreateBreakGlassRecoveryCodesAsync(Guid administratorUserId, int count, TimeSpan validity, CancellationToken ct);
    Task<ManagementUserRecord?> RedeemBreakGlassRecoveryCodeAsync(string username, string recoveryCode, CancellationToken ct);
    Task<ManagementUserRecord> FindOrProvisionExternalUserAsync(string issuer, string subject, string username, string displayName, IReadOnlyList<string> roles, CancellationToken ct);
    Task<AlarmRecord> UpsertAlarmAsync(string fingerprint, string severity, string category, string title, string? details, Guid? agentId, DateTimeOffset nowUtc, CancellationToken ct);
    Task<bool> ResolveAlarmAsync(string fingerprint, DateTimeOffset nowUtc, CancellationToken ct);
    Task<AlarmRecord?> AcknowledgeAlarmAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct);
    Task<AlarmRecord?> UpdateAlarmWorkflowAsync(Guid alarmId, string? assignedTo, string? note, DateTimeOffset? dueAtUtc, string actor, DateTimeOffset nowUtc, CancellationToken ct);
    Task<AlarmRecord?> ResolveAlarmByIdAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct);
    Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(int limit, bool includeResolved, CancellationToken ct);
    Task<ClusterLeaseRecord?> TryAcquireOrRenewClusterLeaseAsync(string leaseName, string ownerId, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken ct);
    Task<ClusterLeaseRecord?> GetClusterLeaseAsync(string leaseName, CancellationToken ct);
}
