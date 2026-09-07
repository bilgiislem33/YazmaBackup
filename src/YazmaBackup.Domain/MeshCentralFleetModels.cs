namespace YazmaBackup.Domain;

public sealed record MeshCentralConnectorRecord(
    Guid ConnectorId,
    string Name,
    string BaseUri,
    string Username,
    string AuthenticationMode,
    string ProtectedCredential,
    string? MeshCtrlPath,
    bool Enabled,
    int SyncIntervalMinutes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastConnectionTestAtUtc,
    bool? LastConnectionTestSucceeded,
    string? LastConnectionTestMessage,
    DateTimeOffset? LastInventorySyncAtUtc,
    string? ServerVersion);

public sealed record MeshCentralInventoryDeviceRecord(
    string NodeId,
    Guid ConnectorId,
    string Name,
    string? Hostname,
    string? Domain,
    string? GroupName,
    bool Online,
    DateTimeOffset ObservedAtUtc,
    Guid? MatchedAgentId,
    string MatchStatus,
    string? MatchEvidence,
    string? AgentVersion);

public sealed record MeshCentralDeploymentRecord(
    Guid DeploymentId,
    Guid ConnectorId,
    string NodeId,
    string DeviceName,
    Guid? AgentId,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Detail,
    string? CommandId);

public sealed record MeshCentralFleetSummary(
    int TotalDevices,
    int OnlineDevices,
    int MatchedAgents,
    int MissingAgents,
    int AmbiguousMatches,
    int PendingDeployments,
    DateTimeOffset? LastInventorySyncAtUtc);
