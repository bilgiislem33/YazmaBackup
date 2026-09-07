namespace YazmaBackup.Domain;

public static class RepositoryHealthStatus
{
    public const string Healthy = "healthy";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

public sealed record RepositoryHealthRecord(
    Guid HealthId,
    Guid PolicyId,
    Guid AgentId,
    string RepositoryId,
    string RepositoryRoot,
    DateTimeOffset MeasuredAtUtc,
    long TotalBytes,
    long FreeBytes,
    long RepositoryPhysicalBytes,
    int RestorePointCount,
    DateTimeOffset? LatestRestorePointUtc,
    long NewBytesLast7Days,
    double DailyGrowthBytes,
    double? EstimatedDaysToFull,
    string Status,
    string? Reason);

public sealed record DisasterRecoverySessionStepRecord(
    int Order,
    string ServiceId,
    string ServiceName,
    IReadOnlyList<string> RequiredDependencies,
    string Status,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string? VerificationNote);

public sealed record DisasterRecoverySessionRecord(
    Guid SessionId,
    string Name,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int CurrentOrder,
    IReadOnlyList<DisasterRecoverySessionStepRecord> Steps);

public sealed record BusinessServiceDependencyRecord(
    Guid DependencyId,
    string ServiceId,
    string DependsOnServiceId,
    string DependencyType,
    bool Required,
    DateTimeOffset CreatedAtUtc);

public sealed record RecoveryPlanRecord(
    Guid PlanId,
    string Name,
    IReadOnlyList<Guid> PolicyIds,
    int MaxParallelAgents,
    int RtoTargetMinutes,
    int IntervalDays,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset NextRunAtUtc);

public sealed record RecoveryRunTargetRecord(
    Guid TargetId,
    Guid PolicyId,
    Guid AgentId,
    Guid? CommandId,
    string Status,
    bool? Succeeded,
    long VerifiedBytes,
    long DurationMilliseconds,
    string? Error);

public sealed record RecoveryRunRecord(
    Guid RunId,
    Guid PlanId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Status,
    int RtoTargetMinutes,
    long VerifiedBytes,
    long LongestRestoreMilliseconds,
    IReadOnlyList<RecoveryRunTargetRecord> Targets);

public sealed record MeshCentralLinkRecord(
    Guid AgentId,
    string MeshCentralBaseUri,
    string NodeId,
    DateTimeOffset LinkedAtUtc,
    DateTimeOffset? LastSynchronizedAtUtc,
    string? LastKnownNodeStatus,
    string? LastDeploymentStatus);

public sealed record NotificationRouteRecord(
    Guid RouteId,
    string Name,
    string Destination,
    string ProtectedHmacSecret,
    IReadOnlyList<string> Severities,
    bool Enabled,
    DateTimeOffset CreatedAtUtc);

public sealed record NotificationDeliveryRecord(
    Guid DeliveryId,
    Guid RouteId,
    Guid AlarmId,
    DateTimeOffset AlarmVersionUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int AttemptCount,
    bool Succeeded,
    string? Error);
