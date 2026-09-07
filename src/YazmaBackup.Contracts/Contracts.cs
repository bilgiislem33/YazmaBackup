using YazmaBackup.Domain;

namespace YazmaBackup.Contracts;

public sealed record RegisterAgentRequest(string MachineName, string OperatingSystem, string AgentVersion, IReadOnlyList<string> Capabilities, string? KeyExchangePublicKeyPem = null);
public sealed record RegisterAgentResponse(Guid AgentId, string AccessToken);
public sealed record CreateEnrollmentTokenRequest(int ValidForMinutes = 15, int MaxUses = 1);
public sealed record CreateEnrollmentTokenResponse(Guid GrantId, string EnrollmentToken, DateTimeOffset ExpiresAtUtc, int MaxUses);
public sealed record HeartbeatRequest(string MachineName, string OperatingSystem, string AgentVersion, IReadOnlyList<string> Capabilities, DateTimeOffset AgentTimeUtc, string? KeyExchangePublicKeyPem = null, ProtectionTelemetryDto? Protection = null, IReadOnlyList<RepositoryCircuitTelemetry>? RepositoryCircuits = null);
public sealed record HeartbeatResponse(DateTimeOffset ServerTimeUtc);

public sealed record AgentTransferTelemetryRequest(
    Guid CommandId,
    string Operation,
    string State,
    string? RepositoryId,
    long BytesTransferred,
    long BytesPerSecond,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset SampledAtUtc,
    string? Stage = null,
    long? LogicalBytesProcessed = null,
    long? LogicalBytesTotal = null,
    int? FilesProcessed = null,
    int? FilesTotal = null);

public sealed record TransferTelemetryPointDto(
    Guid AgentId,
    string MachineName,
    Guid CommandId,
    string Operation,
    string State,
    string? RepositoryId,
    long BytesTransferred,
    long BytesPerSecond,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset SampledAtUtc,
    string? Stage = null,
    long? LogicalBytesProcessed = null,
    long? LogicalBytesTotal = null,
    int? FilesProcessed = null,
    int? FilesTotal = null);

public sealed record TransferTelemetrySeriesPointDto(
    DateTimeOffset SampledAtUtc,
    long BytesPerSecond);

public sealed record TransferTelemetrySnapshotDto(
    DateTimeOffset GeneratedAtUtc,
    long TotalBytesPerSecond,
    int ActiveTransfers,
    IReadOnlyList<TransferTelemetryPointDto> Active,
    IReadOnlyList<TransferTelemetrySeriesPointDto> History);

public sealed record AgentSummaryDto(Guid AgentId, string MachineName, string OperatingSystem, string? AgentVersion, IReadOnlyList<string> Capabilities, DateTimeOffset EnrolledAtUtc, DateTimeOffset LastSeenUtc, string ProtectionStatus = "healthy", string? ProtectionReason = null, DateTimeOffset? ProtectionTriggeredAtUtc = null, Guid? ProtectionIncidentId = null, IReadOnlyList<RepositoryCircuitTelemetry>? RepositoryCircuits = null, string? AssignedUser = null);
public sealed record SetAgentAssignedUserRequest(string? AssignedUser);

public sealed record EnqueueBrowseRequest(string Path);
public sealed record EnqueueBackupRequest(
    string Path,
    string RepositoryRoot,
    string RepositoryId,
    bool RequireSnapshot = true,
    long ActiveBytesPerSecond = 2 * 1024 * 1024,
    long IdleBytesPerSecond = 0,
    int UserIdleThresholdSeconds = 300,
    RetentionPolicy? Retention = null,
    ProtectionPolicy? Protection = null);
public sealed record EnqueueRestoreRequest(string RepositoryRoot, string RepositoryId, string BackupId, string DestinationRoot, bool OverwriteExisting = false);
public sealed record EnqueueListRestorePointsRequest(string RepositoryRoot, string RepositoryId, string SourceRoot);
public sealed record EnqueueListRestoreEntriesRequest(string RepositoryRoot, string RepositoryId, string BackupId, string? Prefix = null);
public sealed record EnqueuePointInTimeRestoreRequest(string RepositoryRoot, string RepositoryId, string SourceRoot, DateTimeOffset RestorePointUtc, string DestinationRoot, bool OverwriteExisting = false);
public sealed record EnqueueScrubRequest(string RepositoryRoot, string RepositoryId, bool MigrateLegacyPlaintext = true);
public sealed record EnqueueRestoreDrillRequest(string RepositoryRoot, string RepositoryId, string SourceRoot);
public sealed record EnqueueStageUpdateRequest(string Version, string PackageUri, string Sha256, string SignatureBase64);
public sealed record ProvisionRepositoryKeyRequest(string RepositoryId, string KeyId, string KeyBase64, bool MakeActive = true);
public sealed record RemoveRepositoryKeyRequest(string RepositoryRoot, string RepositoryId, string KeyId);
public sealed record ProvisionNasCredentialRequest(string RepositoryId, string Username, string Password);
public sealed record NasCredentialSecret(string Username, string Password);
public sealed record WrappedNasCredentialPayload(string RepositoryId, string WrappedCredentialBase64);
public sealed record TestNasAccessRequest(string RepositoryRoot, string RepositoryId);
public sealed record TestNasAccessPayload(string RepositoryRoot, string RepositoryId);
public sealed record SaveGlobalNasProfileRequest(string RepositoryId, string RepositoryRoot, string Username, string? Password);

public sealed record ProvisionAndTestNasCredentialRequest(string RepositoryRoot, string RepositoryId, string Username, string Password);
public sealed record WrappedProvisionAndTestNasCredentialPayload(string RepositoryRoot, string RepositoryId, string WrappedCredentialBase64);

public sealed record NasAccessTestResultDto(bool Succeeded, string RepositoryId, string RepositoryRoot, bool CredentialConfigured, bool DirectoryReadable, bool WriteProbeSucceeded, string Message);

public sealed record EnqueueCommandResponse(Guid CommandId);

public sealed record CreateBackupPolicyRequest(
    string Name,
    Guid AgentId,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    bool RequireSnapshot = true,
    int IntervalMinutes = 60,
    long ActiveBytesPerSecond = 2 * 1024 * 1024,
    long IdleBytesPerSecond = 0,
    int UserIdleThresholdSeconds = 300,
    RetentionPolicy? Retention = null,
    ProtectionPolicy? Protection = null,
    bool Enabled = true,
    int RestoreDrillIntervalDays = 7,
    int RepositoryHealthIntervalHours = 24);
public sealed record BackupPolicyDto(Guid PolicyId, string Name, Guid AgentId, string SourcePath, string RepositoryRoot, string RepositoryId, bool RequireSnapshot, int IntervalMinutes, long ActiveBytesPerSecond, long IdleBytesPerSecond, int UserIdleThresholdSeconds, RetentionPolicy Retention, ProtectionPolicy Protection, bool Enabled, DateTimeOffset? LastScheduledAtUtc, DateTimeOffset NextRunAtUtc, int RestoreDrillIntervalDays, DateTimeOffset? LastRestoreDrillScheduledAtUtc, DateTimeOffset? NextRestoreDrillAtUtc, int RepositoryHealthIntervalHours, DateTimeOffset? LastRepositoryHealthScheduledAtUtc, DateTimeOffset? NextRepositoryHealthAtUtc);
public sealed record SetPolicyEnabledRequest(bool Enabled);

public sealed record AgentCommandDto(Guid CommandId, string Type, string PayloadJson, DateTimeOffset CreatedAtUtc, Guid LeaseId, DateTimeOffset LeaseExpiresAtUtc, int AttemptCount);
public sealed record RenewCommandLeaseRequest(Guid LeaseId);
public sealed record RenewCommandLeaseResponse(Guid LeaseId, DateTimeOffset LeaseExpiresAtUtc);
public sealed record CommandResultRequest(Guid LeaseId, bool Succeeded, string ResultJson, string? Error);
public sealed record CommandResultDto(Guid CommandId, bool Completed, bool Succeeded, string? ResultJson, string? Error, int AttemptCount, DateTimeOffset? LeaseExpiresAtUtc);

public sealed record BrowsePayload(string Path);
public sealed record BackupPayload(string Path, string RepositoryRoot, string RepositoryId, bool RequireSnapshot, long ActiveBytesPerSecond, long IdleBytesPerSecond, int UserIdleThresholdSeconds, RetentionPolicy Retention, ProtectionPolicy? Protection = null);
public sealed record RestorePayload(string RepositoryRoot, string RepositoryId, string BackupId, string DestinationRoot, bool OverwriteExisting);
public sealed record ListRestorePointsPayload(string RepositoryRoot, string RepositoryId, string SourceRoot);
public sealed record ListRestoreEntriesPayload(string RepositoryRoot, string RepositoryId, string BackupId, string? Prefix = null);
public sealed record RestorePointInTimePayload(string RepositoryRoot, string RepositoryId, string SourceRoot, DateTimeOffset RestorePointUtc, string DestinationRoot, bool OverwriteExisting);
public sealed record ScrubRepositoryPayload(string RepositoryRoot, string RepositoryId, bool MigrateLegacyPlaintext);
public sealed record RestoreDrillPayload(string RepositoryRoot, string RepositoryId, string SourceRoot);
public sealed record WrappedRepositoryKeyPayload(string RepositoryId, string KeyId, string WrappedKeyBase64, bool MakeActive);
public sealed record RemoveRepositoryKeyPayload(string RepositoryRoot, string RepositoryId, string KeyId);
public sealed record StageAgentUpdatePayload(string Version, string PackageUri, string Sha256, string SignatureBase64);
public sealed record ApplyStagedAgentUpdatePayload(string Version);
public sealed record ApplyStagedAgentUpdateResultDto(string Version, string UpdaterPath, bool Scheduled, bool IdentityPreserved, bool PoliciesPreserved);
public sealed record ClearProtectionLockRequest(Guid? ExpectedIncidentId = null);
public sealed record ClearProtectionLockPayload(Guid? ExpectedIncidentId = null);
public sealed record ProtectionTelemetryDto(string Status, string? Reason, DateTimeOffset? TriggeredAtUtc, Guid? IncidentId);
public sealed record BrowseEntryDto(string Name, string FullPath, bool IsDirectory, long? Length, DateTimeOffset LastWriteTimeUtc);
public sealed record BrowseResultDto(string Path, IReadOnlyList<BrowseEntryDto> Entries);
public sealed record BackupResultDto(string BackupId, int FileCount, long LogicalBytes, long UploadedBytes, int NewChunks, int ReusedChunks, int ReusedFiles, string ManifestLocation, bool SnapshotBacked, string IncrementalMode, string? EncryptionKeyId, int RetentionDeletedRestorePoints, int RetentionDeletedChunks);
public sealed record RestoreResultDto(string BackupId, int RestoredFiles, long RestoredBytes, string DestinationRoot);
public sealed record RestorePointSummaryDto(string BackupId, DateTimeOffset CreatedAtUtc, long LogicalBytes, int FileCount, bool SnapshotBacked, string IncrementalMode);
public sealed record ListRestorePointsResultDto(string SourceRoot, IReadOnlyList<RestorePointSummaryDto> RestorePoints);
public sealed record RestoreEntryDto(string Name, string RelativePath, bool IsDirectory, long? Length, DateTimeOffset? LastWriteTimeUtc);
public sealed record ListRestoreEntriesResultDto(string BackupId, string Prefix, IReadOnlyList<RestoreEntryDto> Entries);
public sealed record ScrubResultDto(int VerifiedChunks, int MigratedChunks, int VerifiedManifests, long VerifiedLogicalBytes);
public sealed record RestoreDrillResultDto(string BackupId, int VerifiedFiles, long VerifiedBytes, long DurationMilliseconds, bool CleanupSucceeded);
public sealed record StageUpdateResultDto(string Version, string StagedPackagePath, string Sha256, bool SignatureVerified);

public sealed record LoginRequest(string Username, string Password);
public sealed record SessionUserDto(Guid UserId, string Username, string DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword);
public sealed record LoginResponse(SessionUserDto User, string CsrfToken);
public sealed record CreateManagementUserRequest(string Username, string DisplayName, string Password, IReadOnlyList<string> Roles, bool MustChangePassword = true);
public sealed record SetManagementUserEnabledRequest(bool Enabled);
public sealed record ManagementUserDto(Guid UserId, string Username, string DisplayName, IReadOnlyList<string> Roles, bool Enabled, bool MustChangePassword, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastLoginAtUtc, DateTimeOffset? LockedUntilUtc);
public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AuditEventDto(Guid EventId, DateTimeOffset OccurredAtUtc, string Actor, string Action, string Method, string Path, int StatusCode, string? RemoteAddress, string CorrelationId);
public sealed record DashboardSummaryDto(int TotalAgents, int OnlineAgents, int OfflineAgents, int EnabledPolicies, int DisabledPolicies, int PendingCommands, int FailedCommandsLast24Hours, int ProtectionLockedAgents, DateTimeOffset GeneratedAtUtc);
public sealed record OperationalHealthDto(int Score, string Grade, int SuccessfulBackups24Hours, int FailedBackups24Hours, int SuccessfulRestoreDrills30Days, int FailedRestoreDrills30Days, int OverduePolicies, int ProtectionLockedAgents, int OfflineAgents, DateTimeOffset GeneratedAtUtc);
