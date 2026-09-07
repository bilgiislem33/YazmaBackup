using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IMeshCentralFleetStore
{
    Task<MeshCentralConnectorRecord?> GetMeshCentralConnectorAsync(CancellationToken ct);
    Task<MeshCentralConnectorRecord> SaveMeshCentralConnectorAsync(MeshCentralConnectorRecord connector, CancellationToken ct);
    Task SaveMeshCentralInventoryAsync(Guid connectorId, IReadOnlyList<MeshCentralInventoryDeviceRecord> devices, DateTimeOffset syncedAtUtc, string? serverVersion, CancellationToken ct);
    Task<IReadOnlyList<MeshCentralInventoryDeviceRecord>> GetMeshCentralInventoryAsync(CancellationToken ct);
    Task<MeshCentralFleetSummary> GetMeshCentralFleetSummaryAsync(CancellationToken ct);
    Task<MeshCentralDeploymentRecord> AddMeshCentralDeploymentAsync(MeshCentralDeploymentRecord deployment, CancellationToken ct);
    Task<MeshCentralDeploymentRecord> UpdateMeshCentralDeploymentAsync(Guid deploymentId, string status, string? detail, string? commandId, Guid? agentId, CancellationToken ct);
    Task<IReadOnlyList<MeshCentralDeploymentRecord>> GetMeshCentralDeploymentsAsync(int limit, CancellationToken ct);

    // R5.10: lets the heartbeat-driven immediate reconciliation path (Program.cs /api/v1/agent/heartbeat)
    // mark a single inventory device as matched without waiting for the next full MeshCentral ListDevices
    // sync. Without this, a deployment can flip to "succeeded" while the Fleet UI still shows the device
    // as "Agent Yok" until the next scheduled/fallback SynchronizeAsync call repopulates MatchStatus.
    Task MarkMeshCentralDeviceMatchedAsync(string nodeId, Guid agentId, string? agentVersion, string evidence, CancellationToken ct);
}
