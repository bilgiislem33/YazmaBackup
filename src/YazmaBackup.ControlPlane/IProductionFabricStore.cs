using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IProductionFabricStore
{
    Task<RecoveryRunbookRecord> CreateRecoveryRunbookAsync(string name, IReadOnlyList<Guid> recoveryPlanIds, int rtoBudgetMinutes, int intervalDays, bool enabled, CancellationToken ct);
    Task<IReadOnlyList<RecoveryRunbookRecord>> GetRecoveryRunbooksAsync(CancellationToken ct);
    Task<IReadOnlyList<RecoveryRunbookRunRecord>> GetRecoveryRunbookRunsAsync(int limit, CancellationToken ct);
    Task<int> AdvanceProductionFabricAsync(DateTimeOffset nowUtc, CancellationToken ct);
    Task<(IntegrationCredentialRecord Record, string PlaintextToken)> CreateIntegrationCredentialAsync(string name, string purpose, TimeSpan validity, CancellationToken ct);
    Task<IntegrationCredentialRecord?> AuthenticateIntegrationCredentialAsync(string plaintextToken, string purpose, CancellationToken ct);
    Task<IReadOnlyList<IntegrationCredentialRecord>> GetIntegrationCredentialsAsync(CancellationToken ct);
    Task<bool> RevokeIntegrationCredentialAsync(Guid credentialId, CancellationToken ct);
    Task<MeshCentralSyncEventRecord> ReportMeshCentralSyncAsync(Guid credentialId, Guid eventId, Guid agentId, string nodeId, string nodeStatus, string? deploymentStatus, DateTimeOffset reportedAtUtc, DateTimeOffset receivedAtUtc, CancellationToken ct);
    Task<IReadOnlyList<MeshCentralSyncEventRecord>> GetMeshCentralSyncEventsAsync(int limit, CancellationToken ct);
}
