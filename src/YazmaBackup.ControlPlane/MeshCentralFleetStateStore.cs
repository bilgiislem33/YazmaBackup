using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<MeshCentralConnectorRecord?> GetMeshCentralConnectorAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.MeshCentralConnectors.Values.OrderBy(x => x.CreatedAtUtc).FirstOrDefault(); }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralConnectorRecord> SaveMeshCentralConnectorAsync(MeshCentralConnectorRecord connector, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            next.MeshCentralConnectors.Clear();
            next.MeshCentralConnectors[connector.ConnectorId] = connector;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return connector;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveMeshCentralInventoryAsync(Guid connectorId, IReadOnlyList<MeshCentralInventoryDeviceRecord> devices, DateTimeOffset syncedAtUtc, string? serverVersion, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.MeshCentralConnectors.TryGetValue(connectorId, out var connector)) throw new KeyNotFoundException("MeshCentral connector not found.");
            var next = CloneState();
            next.MeshCentralInventoryDevices.Clear();
            foreach (var device in devices) next.MeshCentralInventoryDevices[device.NodeId] = device;
            next.MeshCentralConnectors[connectorId] = connector with { LastInventorySyncAtUtc = syncedAtUtc, ServerVersion = serverVersion ?? connector.ServerVersion, UpdatedAtUtc = syncedAtUtc };
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MeshCentralInventoryDeviceRecord>> GetMeshCentralInventoryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.MeshCentralInventoryDevices.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralFleetSummary> GetMeshCentralFleetSummaryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var devices = _state.MeshCentralInventoryDevices.Values.ToArray();
            var lastSync = _state.MeshCentralConnectors.Values.Select(x => x.LastInventorySyncAtUtc).Where(x => x.HasValue).OrderByDescending(x => x).FirstOrDefault();
            return new MeshCentralFleetSummary(
                devices.Length,
                devices.Count(x => x.Online),
                devices.Count(x => x.MatchStatus == "matched"),
                devices.Count(x => x.MatchStatus == "missing"),
                devices.Count(x => x.MatchStatus == "ambiguous"),
                _state.MeshCentralDeployments.Values.Count(x => x.Status is "queued" or "dispatching" or "dispatched" or "installing"),
                lastSync);
        }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralDeploymentRecord> AddMeshCentralDeploymentAsync(MeshCentralDeploymentRecord deployment, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = CloneState();
            next.MeshCentralDeployments[deployment.DeploymentId] = deployment;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return deployment;
        }
        finally { _gate.Release(); }
    }

    public async Task<MeshCentralDeploymentRecord> UpdateMeshCentralDeploymentAsync(Guid deploymentId, string status, string? detail, string? commandId, Guid? agentId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.MeshCentralDeployments.TryGetValue(deploymentId, out var current)) throw new KeyNotFoundException("MeshCentral deployment not found.");
            var updated = current with { Status = status, Detail = detail, CommandId = commandId ?? current.CommandId, AgentId = agentId ?? current.AgentId, UpdatedAtUtc = DateTimeOffset.UtcNow };
            var next = CloneState();
            next.MeshCentralDeployments[deploymentId] = updated;
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MeshCentralDeploymentRecord>> GetMeshCentralDeploymentsAsync(int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.MeshCentralDeployments.Values.OrderByDescending(x => x.RequestedAtUtc).Take(Math.Clamp(limit, 1, 5000)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task MarkMeshCentralDeviceMatchedAsync(string nodeId, Guid agentId, string? agentVersion, string evidence, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.MeshCentralInventoryDevices.TryGetValue(nodeId, out var device)) return;
            var next = CloneState();
            next.MeshCentralInventoryDevices[nodeId] = device with
            {
                MatchedAgentId = agentId,
                MatchStatus = "matched",
                MatchEvidence = evidence,
                AgentVersion = agentVersion ?? device.AgentVersion
            };
            await CommitUnsafeAsync(next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
