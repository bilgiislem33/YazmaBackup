namespace YazmaBackup.Contracts;

public sealed record SaveMeshCentralConnectorRequest(
    string Name,
    string BaseUri,
    string Username,
    string AuthenticationMode,
    string? Credential,
    string? MeshCtrlPath,
    bool Enabled = true,
    int SyncIntervalMinutes = 5);

public sealed record MeshCentralConnectorDto(
    Guid ConnectorId,
    string Name,
    string BaseUri,
    string Username,
    string AuthenticationMode,
    bool CredentialConfigured,
    string? MeshCtrlPath,
    bool Enabled,
    int SyncIntervalMinutes,
    DateTimeOffset? LastConnectionTestAtUtc,
    bool? LastConnectionTestSucceeded,
    string? LastConnectionTestMessage,
    DateTimeOffset? LastInventorySyncAtUtc,
    string? ServerVersion);

public sealed record MeshCentralConnectionTestDto(bool Succeeded, string Message, string? ServerVersion, string? ResolvedMeshCtrlPath);
public sealed record MeshCentralFleetSummaryDto(int TotalDevices, int OnlineDevices, int MatchedAgents, int MissingAgents, int AmbiguousMatches, int PendingDeployments, DateTimeOffset? LastInventorySyncAtUtc);
public sealed record MeshCentralInventoryDeviceDto(string NodeId, string Name, string? Hostname, string? Domain, string? GroupName, bool Online, DateTimeOffset ObservedAtUtc, Guid? MatchedAgentId, string MatchStatus, string? MatchEvidence, string? AgentVersion);
public sealed record MeshCentralDeploymentDto(Guid DeploymentId, string NodeId, string DeviceName, Guid? AgentId, string Status, DateTimeOffset RequestedAtUtc, DateTimeOffset UpdatedAtUtc, string? Detail, string? CommandId);
public sealed record DeployMeshCentralAgentsRequest(IReadOnlyList<string> NodeIds);
