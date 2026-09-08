using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AgentRuntimeEndpoints
{
    internal static IEndpointRouteBuilder MapAgentRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(AgentEndpointContracts.Heartbeat, async (
            HttpContext http,
            HeartbeatRequest request,
            IControlPlaneStore store,
            IMeshCentralFleetStore fleet,
            GlobalNasProfileService globalNas,
            CancellationToken ct) =>
        {
            var auth = await AgentRequestSecurity.AuthenticateAsync(http, store, ct).ConfigureAwait(false);
            if (auth is null) return Results.Unauthorized();
            if (!AgentRequestSecurity.ValidMachine(request.MachineName) ||
                !AgentRequestSecurity.ValidText(request.OperatingSystem, 256) ||
                !AgentRequestSecurity.ValidText(request.AgentVersion, 64) ||
                (request.Capabilities?.Count ?? 0) > 64 ||
                !AgentRequestSecurity.ValidOptionalPublicKey(request.KeyExchangePublicKeyPem))
            {
                return Results.BadRequest(new { error = "Invalid heartbeat payload." });
            }

            var machineName = request.MachineName.Trim();
            var agentVersion = request.AgentVersion.Trim();
            await store.TouchAsync(
                auth.Value.AgentId,
                machineName,
                request.OperatingSystem.Trim(),
                agentVersion,
                AgentRequestSecurity.SanitizeCapabilities(request.Capabilities),
                request.KeyExchangePublicKeyPem?.Trim(),
                AgentRequestSecurity.SanitizeProtection(request.Protection),
                AgentRequestSecurity.SanitizeRepositoryCircuits(request.RepositoryCircuits),
                ct).ConfigureAwait(false);

            await globalNas.ApplyCurrentToAgentAsync(auth.Value.AgentId, ct).ConfigureAwait(false);

            var activeDeployments = await fleet.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false);
            var exactDeployments = activeDeployments
                .Where(x => x.AgentId == auth.Value.AgentId && x.Status is ("dispatching" or "dispatched" or "installing"))
                .ToArray();

            foreach (var deployment in exactDeployments)
            {
                await fleet.UpdateMeshCentralDeploymentAsync(
                    deployment.DeploymentId,
                    "succeeded",
                    $"Authenticated heartbeat completed exact deployment binding. Machine={machineName}; Version={agentVersion}",
                    deployment.CommandId,
                    auth.Value.AgentId,
                    ct).ConfigureAwait(false);
                await fleet.MarkMeshCentralDeviceMatchedAsync(
                    deployment.NodeId,
                    auth.Value.AgentId,
                    agentVersion,
                    $"deployment:{deployment.DeploymentId:N} → agent:{auth.Value.AgentId}",
                    ct).ConfigureAwait(false);
            }

            var inventory = await fleet.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
            static string NormalizeHost(string? value) => (value ?? string.Empty).Trim().Split('.')[0];
            static bool SameHost(string? left, string? right) =>
                !string.IsNullOrWhiteSpace(left) &&
                !string.IsNullOrWhiteSpace(right) &&
                string.Equals(NormalizeHost(left), NormalizeHost(right), StringComparison.OrdinalIgnoreCase);

            var hostCandidates = inventory
                .Where(x => SameHost(x.Hostname, machineName) || SameHost(x.Name, machineName))
                .Select(x => x.NodeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (exactDeployments.Length == 0 && hostCandidates.Length == 1)
            {
                var reconciledAny = false;
                foreach (var deployment in activeDeployments.Where(x =>
                             x.NodeId == hostCandidates[0] &&
                             x.Status is ("dispatching" or "dispatched" or "installing")))
                {
                    await fleet.UpdateMeshCentralDeploymentAsync(
                        deployment.DeploymentId,
                        "succeeded",
                        $"Agent heartbeat reconciled deployment immediately. Machine={machineName}; Version={agentVersion}",
                        deployment.CommandId,
                        auth.Value.AgentId,
                        ct).ConfigureAwait(false);
                    reconciledAny = true;
                }

                if (reconciledAny)
                {
                    await fleet.MarkMeshCentralDeviceMatchedAsync(
                        hostCandidates[0],
                        auth.Value.AgentId,
                        agentVersion,
                        $"hostname:{machineName} → {machineName} (heartbeat)",
                        ct).ConfigureAwait(false);
                }
            }

            return Results.Ok(new HeartbeatResponse(DateTimeOffset.UtcNow));
        });

        endpoints.MapPost(AgentEndpointContracts.TransferTelemetry, async (
            HttpContext http,
            AgentTransferTelemetryRequest request,
            IControlPlaneStore store,
            TransferTelemetryRegistry registry,
            CancellationToken ct) =>
        {
            var auth = await AgentRequestSecurity.AuthenticateAsync(http, store, ct).ConfigureAwait(false);
            if (auth is null) return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            if (request.CommandId == Guid.Empty ||
                !string.Equals(request.Operation, "backup", StringComparison.Ordinal) ||
                request.State is not ("active" or "completed" or "failed") ||
                (request.RepositoryId is not null && !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128)) ||
                request.BytesTransferred < 0 ||
                request.BytesPerSecond < 0 ||
                request.BytesPerSecond > 8L * 1024 * 1024 * 1024 ||
                request.SampledAtUtc < now.AddMinutes(-5) ||
                request.SampledAtUtc > now.AddSeconds(15) ||
                request.StartedAtUtc > request.SampledAtUtc ||
                request.StartedAtUtc < request.SampledAtUtc.AddDays(-2) ||
                (request.Stage is not null && request.Stage is not ("preparing" or "scanning" or "processing" or "committing" or "completed" or "failed")) ||
                request.LogicalBytesProcessed is < 0 ||
                request.LogicalBytesTotal is < 0 ||
                request.FilesProcessed is < 0 ||
                request.FilesTotal is < 0 ||
                (request.LogicalBytesProcessed.HasValue && request.LogicalBytesTotal.HasValue && request.LogicalBytesProcessed > request.LogicalBytesTotal) ||
                (request.FilesProcessed.HasValue && request.FilesTotal.HasValue && request.FilesProcessed > request.FilesTotal))
            {
                return Results.BadRequest(new { error = "Invalid transfer telemetry payload." });
            }

            registry.Record(auth.Value.AgentId, auth.Value.Record.MachineName, request);
            return Results.NoContent();
        });

        return endpoints;
    }
}
