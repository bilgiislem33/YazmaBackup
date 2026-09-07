namespace YazmaBackup.Contracts;

public sealed record RepositoryHealthScanPayload(Guid PolicyId, string RepositoryRoot, string RepositoryId);
public sealed record RepositoryHealthScanResultDto(Guid PolicyId, string RepositoryRoot, string RepositoryId, DateTimeOffset MeasuredAtUtc, long TotalBytes, long FreeBytes, long RepositoryPhysicalBytes, int RestorePointCount, DateTimeOffset? LatestRestorePointUtc, long NewBytesLast7Days, double DailyGrowthBytes, double? EstimatedDaysToFull, string Status, string? Reason);
public sealed record RepositoryHealthDto(Guid HealthId, Guid PolicyId, Guid AgentId, string RepositoryId, string RepositoryRoot, DateTimeOffset MeasuredAtUtc, long TotalBytes, long FreeBytes, long RepositoryPhysicalBytes, int RestorePointCount, DateTimeOffset? LatestRestorePointUtc, long NewBytesLast7Days, double DailyGrowthBytes, double? EstimatedDaysToFull, string Status, string? Reason);

public sealed record CreateRecoveryPlanRequest(string Name, IReadOnlyList<Guid> PolicyIds, int MaxParallelAgents = 3, int RtoTargetMinutes = 60, int IntervalDays = 30, bool Enabled = true);
public sealed record RecoveryPlanDto(Guid PlanId, string Name, IReadOnlyList<Guid> PolicyIds, int MaxParallelAgents, int RtoTargetMinutes, int IntervalDays, bool Enabled, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastRunAtUtc, DateTimeOffset NextRunAtUtc);
public sealed record RecoveryRunTargetDto(Guid TargetId, Guid PolicyId, Guid AgentId, Guid? CommandId, string Status, bool? Succeeded, long VerifiedBytes, long DurationMilliseconds, string? Error);
public sealed record RecoveryRunDto(Guid RunId, Guid PlanId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, string Status, int RtoTargetMinutes, long VerifiedBytes, long LongestRestoreMilliseconds, IReadOnlyList<RecoveryRunTargetDto> Targets);

public sealed record LinkMeshCentralNodeRequest(string MeshCentralBaseUri, string NodeId);
public sealed record MeshCentralLinkDto(Guid AgentId, string MeshCentralBaseUri, string NodeId, DateTimeOffset LinkedAtUtc, DateTimeOffset? LastSynchronizedAtUtc, string? LastKnownNodeStatus, string? LastDeploymentStatus);

public sealed record CreateNotificationRouteRequest(string Name, string Destination, string HmacSecret, IReadOnlyList<string>? Severities = null, bool Enabled = true);
public sealed record NotificationRouteDto(Guid RouteId, string Name, string Destination, IReadOnlyList<string> Severities, bool Enabled, DateTimeOffset CreatedAtUtc);
public sealed record NotificationDeliveryDto(Guid DeliveryId, Guid RouteId, Guid AlarmId, DateTimeOffset AlarmVersionUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, int AttemptCount, bool Succeeded, string? Error);
