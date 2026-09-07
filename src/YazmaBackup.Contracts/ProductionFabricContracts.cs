namespace YazmaBackup.Contracts;

public sealed record CreateRecoveryRunbookRequest(string Name, IReadOnlyList<Guid> RecoveryPlanIds, int RtoBudgetMinutes = 240, int IntervalDays = 30, bool Enabled = true);
public sealed record RecoveryRunbookDto(Guid RunbookId, string Name, IReadOnlyList<Guid> RecoveryPlanIds, int RtoBudgetMinutes, int IntervalDays, bool Enabled, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastRunAtUtc, DateTimeOffset NextRunAtUtc);
public sealed record RecoveryRunbookStepDto(int StepIndex, Guid RecoveryPlanId, Guid? RecoveryRunId, string Status, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, string? Error);
public sealed record RecoveryRunbookRunDto(Guid RunId, Guid RunbookId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, string Status, int RtoBudgetMinutes, int CurrentStepIndex, IReadOnlyList<RecoveryRunbookStepDto> Steps);

public sealed record CreateIntegrationCredentialRequest(string Name, string Purpose = "meshcentral-status", int ValidForDays = 90);
public sealed record CreateIntegrationCredentialResponse(Guid CredentialId, string Token, string Name, string Purpose, DateTimeOffset ExpiresAtUtc);
public sealed record IntegrationCredentialDto(Guid CredentialId, string Name, string Purpose, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, bool Revoked);
public sealed record MeshCentralStatusReportRequest(Guid EventId, Guid AgentId, string NodeId, string NodeStatus, string? DeploymentStatus, DateTimeOffset ReportedAtUtc);
public sealed record MeshCentralSyncEventDto(Guid EventId, Guid AgentId, string NodeId, string NodeStatus, string? DeploymentStatus, DateTimeOffset ReportedAtUtc, DateTimeOffset ReceivedAtUtc, Guid CredentialId);

public sealed record RecoveryEvidenceEnvelopeDto(string EvidenceType, string ProductVersion, DateTimeOffset GeneratedAtUtc, string PayloadJson, string PayloadSha256, string SignatureAlgorithm, string SignatureBase64, string PublicKeyPem);
