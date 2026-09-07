namespace YazmaBackup.Domain;

public sealed record ManagementApiTokenRecord(
    Guid TokenId,
    string Name,
    string TokenHash,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    bool Revoked);

public sealed record BreakGlassRecoveryCodeRecord(
    Guid CodeId,
    Guid UserId,
    string CodeHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? UsedAtUtc);

public sealed record ExternalIdentityRecord(
    Guid IdentityId,
    Guid UserId,
    string Issuer,
    string Subject,
    DateTimeOffset LinkedAtUtc,
    DateTimeOffset LastLoginAtUtc);

public static class AlarmSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Info, Warning, Critical };
}

public static class AlarmStatus
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
}

public sealed record AlarmRecord(
    Guid AlarmId,
    string Fingerprint,
    string Severity,
    string Category,
    string Title,
    string? Details,
    string Status,
    Guid? AgentId,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    string? AcknowledgedBy,
    DateTimeOffset? ResolvedAtUtc,
    string? AssignedTo = null,
    string? OperatorNote = null,
    DateTimeOffset? DueAtUtc = null,
    DateTimeOffset? WorkflowUpdatedAtUtc = null);

public sealed record ClusterLeaseRecord(
    string LeaseName,
    string OwnerId,
    Guid LeaseId,
    long Epoch,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);
