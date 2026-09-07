namespace YazmaBackup.Domain;

public enum AgentStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public enum AgentCommandType
{
    BrowsePath = 1,
    BackupPath = 2,
    RestoreBackup = 3,
    StageAgentUpdate = 4,
    RestorePointInTime = 5,
    ScrubRepository = 6,
    ProvisionRepositoryKey = 7,
    RemoveRepositoryKey = 8,
    ClearProtectionLock = 9,
    RestoreDrill = 10,
    RepositoryHealthScan = 11,
    ListRestorePoints = 12,
    PilotReadinessProbe = 13,
    DeepValidationProbe = 14,
    GranularRestore = 15,
    RestoreSandbox = 16,
    SelfHealingDiagnose = 17,
    SelfHealingApply = 18,
    ListRestoreEntries = 19,
    ProvisionNasCredential = 20,
    TestNasAccess = 21,
    ProvisionAndTestNasCredential = 22,
    ApplyStagedAgentUpdate = 23
}

public sealed record EnrollmentGrant(
    Guid GrantId,
    string TokenHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int RemainingUses);

public sealed record AgentRecord(
    Guid AgentId,
    string MachineName,
    string OperatingSystem,
    string TokenHash,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset LastSeenUtc,
    string? AgentVersion,
    IReadOnlyList<string>? Capabilities,
    string? KeyExchangePublicKeyPem = null,
    string ProtectionStatus = "healthy",
    string? ProtectionReason = null,
    DateTimeOffset? ProtectionTriggeredAtUtc = null,
    Guid? ProtectionIncidentId = null,
    IReadOnlyList<RepositoryCircuitTelemetry>? RepositoryCircuits = null,
    string? AssignedUser = null);

public sealed record AgentCommand(
    Guid CommandId,
    Guid AgentId,
    AgentCommandType Type,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultJson,
    bool Succeeded,
    string? Error,
    Guid? LeaseId,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? LastLeaseRenewalUtc,
    int AttemptCount,
    string? IdempotencyKey);

public sealed record BackupChunkRef(string Sha256, int Length);

public sealed record BackupFileEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    IReadOnlyList<BackupChunkRef> Chunks);

public sealed record BackupManifest(
    string SchemaVersion,
    string BackupId,
    string AgentId,
    string SourceRoot,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupFileEntry> Files,
    long LogicalBytes,
    long UploadedBytes,
    int NewChunks,
    int ReusedChunks,
    string ChunkerId,
    bool SnapshotBacked,
    string? ParentBackupId = null,
    string IncrementalMode = "full-scan",
    int ReusedFiles = 0,
    string? EncryptionKeyId = null);



public sealed record ProtectionPolicy(
    bool Enabled = true,
    int MinimumChangedFiles = 100,
    double ChangedFileRatioThreshold = 0.35,
    int MinimumExtensionChanges = 25,
    double ExtensionChangeRatioThreshold = 0.20,
    int MinimumEntropySamples = 12,
    double HighEntropyRatioThreshold = 0.35,
    double EntropyThresholdBitsPerByte = 7.45,
    bool AutoLockOnDetection = true)
{
    public void Validate()
    {
        if (MinimumChangedFiles is < 10 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MinimumChangedFiles));
        if (ChangedFileRatioThreshold is < 0.01 or > 1.0) throw new ArgumentOutOfRangeException(nameof(ChangedFileRatioThreshold));
        if (MinimumExtensionChanges is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MinimumExtensionChanges));
        if (ExtensionChangeRatioThreshold is < 0.01 or > 1.0) throw new ArgumentOutOfRangeException(nameof(ExtensionChangeRatioThreshold));
        if (MinimumEntropySamples is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(MinimumEntropySamples));
        if (HighEntropyRatioThreshold is < 0.01 or > 1.0) throw new ArgumentOutOfRangeException(nameof(HighEntropyRatioThreshold));
        if (EntropyThresholdBitsPerByte is < 6.0 or > 8.0) throw new ArgumentOutOfRangeException(nameof(EntropyThresholdBitsPerByte));
    }
}

public sealed record ProtectionAssessment(
    bool Suspicious,
    string Reason,
    int PreviousFileCount,
    int CurrentFileCount,
    int ChangedFileCount,
    int ExtensionChangeCount,
    int EntropySampleCount,
    int HighEntropySampleCount,
    double ChangedFileRatio,
    double ExtensionChangeRatio,
    double HighEntropyRatio);

public sealed record RetentionPolicy(
    int KeepLast = 30,
    int KeepDaily = 14,
    int KeepWeekly = 8,
    int KeepMonthly = 12,
    int ImmutabilityHours = 24)
{
    public void Validate()
    {
        if (KeepLast is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(KeepLast));
        if (KeepDaily is < 0 or > 3650) throw new ArgumentOutOfRangeException(nameof(KeepDaily));
        if (KeepWeekly is < 0 or > 520) throw new ArgumentOutOfRangeException(nameof(KeepWeekly));
        if (KeepMonthly is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(KeepMonthly));
        if (ImmutabilityHours is < 0 or > 8760) throw new ArgumentOutOfRangeException(nameof(ImmutabilityHours));
    }
}

public sealed record BackupPolicyRecord(
    Guid PolicyId,
    string Name,
    Guid AgentId,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    bool RequireSnapshot,
    int IntervalMinutes,
    long ActiveBytesPerSecond,
    long IdleBytesPerSecond,
    int UserIdleThresholdSeconds,
    RetentionPolicy Retention,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastScheduledAtUtc,
    DateTimeOffset NextRunAtUtc,
    ProtectionPolicy? Protection = null,
    int RestoreDrillIntervalDays = 7,
    DateTimeOffset? LastRestoreDrillScheduledAtUtc = null,
    DateTimeOffset? NextRestoreDrillAtUtc = null,
    int RepositoryHealthIntervalHours = 24,
    DateTimeOffset? LastRepositoryHealthScheduledAtUtc = null,
    DateTimeOffset? NextRepositoryHealthAtUtc = null);

public static class ManagementRoles
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string BackupAdministrator = "backup-admin";
    public const string SecurityAdministrator = "security-admin";
    public const string Administrator = "administrator";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Viewer, Operator, BackupAdministrator, SecurityAdministrator, Administrator
    };
}

public sealed record ManagementUserRecord(
    Guid UserId,
    string Username,
    string DisplayName,
    string PasswordHashBase64,
    string PasswordSaltBase64,
    int PasswordIterations,
    IReadOnlyList<string> Roles,
    bool Enabled,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    int FailedLoginCount,
    DateTimeOffset? LockedUntilUtc);

public sealed record AuditEventRecord(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string Action,
    string Method,
    string Path,
    int StatusCode,
    string? RemoteAddress,
    string CorrelationId);

public sealed record OperationalCommandMetrics(
    int SuccessfulBackups,
    int FailedBackups,
    int SuccessfulRestoreDrills,
    int FailedRestoreDrills);
