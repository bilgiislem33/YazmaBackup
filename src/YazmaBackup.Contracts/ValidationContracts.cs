using YazmaBackup.Domain;

namespace YazmaBackup.Contracts;

public sealed record DeepValidationProbePayload(
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    bool RequireSnapshot = true,
    long MinimumFreeBytes = 20L * 1024 * 1024 * 1024);

public sealed record ValidationCheckDto(
    string CheckId,
    string Category,
    string Name,
    string Status,
    string Message,
    long DurationMs,
    string? Detail = null,
    string? RecommendedAction = null);

public sealed record DeepValidationResultDto(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int Score,
    string Grade,
    string MachineName,
    IReadOnlyList<ValidationCheckDto> Checks);

public sealed record EnqueueGranularRestoreRequest(
    string RepositoryRoot,
    string RepositoryId,
    string BackupId,
    string DestinationRoot,
    IReadOnlyList<string> IncludePaths,
    bool OverwriteExisting = false);

public sealed record GranularRestorePayload(
    string RepositoryRoot,
    string RepositoryId,
    string BackupId,
    string DestinationRoot,
    IReadOnlyList<string> IncludePaths,
    bool OverwriteExisting);

public sealed record EnqueueRestoreSandboxRequest(
    string RepositoryRoot,
    string RepositoryId,
    string BackupId,
    string SandboxRoot,
    int MaxFiles = 5000,
    long MaxBytes = 20L * 1024 * 1024 * 1024);

public sealed record RestoreSandboxPayload(
    string RepositoryRoot,
    string RepositoryId,
    string BackupId,
    string SandboxRoot,
    int MaxFiles,
    long MaxBytes);

public sealed record RestoreSandboxResultDto(
    string BackupId,
    string SandboxPath,
    int VerifiedFiles,
    long VerifiedBytes,
    string VerificationStatus,
    DateTimeOffset CompletedAtUtc);

public sealed record SelfHealingDiagnoseRequest(string SourcePath, string RepositoryRoot, string RepositoryId);
public sealed record SelfHealingDiagnosePayload(string SourcePath, string RepositoryRoot, string RepositoryId);

public sealed record SelfHealingActionDto(
    string ActionId,
    string Name,
    string Description,
    bool SafeAutoApply,
    string RiskLevel);

public sealed record SelfHealingDiagnosisDto(
    DateTimeOffset MeasuredAtUtc,
    string OverallStatus,
    IReadOnlyList<ValidationCheckDto> Findings,
    IReadOnlyList<SelfHealingActionDto> Actions);

public sealed record SelfHealingApplyRequest(string RepositoryId, string ActionId, string? WorkingRoot = null);
public sealed record SelfHealingApplyPayload(string RepositoryId, string ActionId, string? WorkingRoot = null);
public sealed record SelfHealingApplyResultDto(string ActionId, bool Applied, string Message, DateTimeOffset CompletedAtUtc);
