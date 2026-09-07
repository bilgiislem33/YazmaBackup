using YazmaBackup.Domain;

namespace YazmaBackup.Contracts;

public sealed record PolicyTemplateDto(
    string TemplateId,
    string Name,
    string Description,
    string RecommendedFor,
    int IntervalMinutes,
    long ActiveBytesPerSecond,
    long IdleBytesPerSecond,
    int RestoreDrillIntervalDays,
    int RepositoryHealthIntervalHours,
    RetentionPolicy Retention,
    ProtectionPolicy Protection);

public sealed record ApplyPolicyTemplateRequest(
    Guid AgentId,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    string? PolicyName = null,
    bool RequireSnapshot = true,
    bool Enabled = true);

public sealed record PilotReadinessProbePayload(
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    bool RequireSnapshot = true,
    long MinimumFreeBytes = 20L * 1024 * 1024 * 1024);

public sealed record PilotReadinessCheckDto(
    string CheckId,
    string Name,
    string Status,
    string Message,
    int Weight,
    string? Detail = null);

public sealed record PilotReadinessProbeResultDto(
    DateTimeOffset MeasuredAtUtc,
    int Score,
    string Grade,
    string MachineName,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    IReadOnlyList<PilotReadinessCheckDto> Checks);

public sealed record PilotReadinessSummaryDto(
    DateTimeOffset GeneratedAtUtc,
    int Score,
    string Grade,
    int TotalAgents,
    int OnlineAgents,
    int EnabledPolicies,
    int CriticalAlarms,
    int ProtectionLockedAgents,
    int HealthyRepositories,
    int FailedRecoveryRuns,
    IReadOnlyList<PilotReadinessCheckDto> Checks,
    IReadOnlyList<string> NextActions);


public sealed record BulkApplyPolicyTemplateRequest(
    IReadOnlyList<Guid> AgentIds,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    string? PolicyNamePrefix = null,
    bool RequireSnapshot = true,
    bool Enabled = true);

public sealed record BulkApplyPolicyTemplateResultDto(
    string TemplateId,
    int CreatedCount,
    IReadOnlyList<BackupPolicyDto> Policies);
