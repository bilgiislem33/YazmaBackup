namespace YazmaBackup.Domain;

public sealed record RepositoryCircuitTelemetry(
    string RepositoryId,
    int ConsecutiveFailures,
    DateTimeOffset? LastFailureAtUtc,
    DateTimeOffset? OpenUntilUtc,
    string? LastError)
{
    public bool IsOpen(DateTimeOffset nowUtc) => OpenUntilUtc is { } until && until > nowUtc;
}

public sealed record RecoveryRunbookRecord(
    Guid RunbookId,
    string Name,
    IReadOnlyList<Guid> RecoveryPlanIds,
    int RtoBudgetMinutes,
    int IntervalDays,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset NextRunAtUtc);

public sealed record RecoveryRunbookStepRecord(
    int StepIndex,
    Guid RecoveryPlanId,
    Guid? RecoveryRunId,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error);

public sealed record RecoveryRunbookRunRecord(
    Guid RunId,
    Guid RunbookId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Status,
    int RtoBudgetMinutes,
    int CurrentStepIndex,
    IReadOnlyList<RecoveryRunbookStepRecord> Steps);

public sealed record IntegrationCredentialRecord(
    Guid CredentialId,
    string Name,
    string Purpose,
    string TokenHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    bool Revoked);

public sealed record MeshCentralSyncEventRecord(
    Guid EventId,
    Guid AgentId,
    string NodeId,
    string NodeStatus,
    string? DeploymentStatus,
    DateTimeOffset ReportedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    Guid CredentialId);
