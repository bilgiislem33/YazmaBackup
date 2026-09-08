using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class MeshCentralManagementEndpoints
{
    internal static AdminEndpointGroups MapMeshCentralManagementEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/meshcentral/connector", async (IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            return connector is null ? Results.NoContent() : Results.Ok(ToMeshConnectorDto(connector));
        });

        groups.Security.MapPut("/meshcentral/connector", async (SaveMeshCentralConnectorRequest request, MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(request.BaseUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                return Results.BadRequest(new { error = "MeshCentral adresi mutlak HTTPS URI olmalıdır." });
            if (!ValidText(request.Name, 100) || !ValidText(request.Username, 256)) return Results.BadRequest(new { error = "Connector adı veya kullanıcı adı geçersiz." });
            var mode = (request.AuthenticationMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not ("password" or "loginkey")) return Results.BadRequest(new { error = "AuthenticationMode password veya loginkey olmalıdır." });
            if (request.SyncIntervalMinutes is < 1 or > 1440) return Results.BadRequest(new { error = "Senkronizasyon aralığı 1-1440 dakika olmalıdır." });
            var existing = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            var protectedCredential = !string.IsNullOrWhiteSpace(request.Credential)
                ? connectorService.ProtectCredential(request.Credential)
                : existing?.ProtectedCredential;
            if (string.IsNullOrWhiteSpace(protectedCredential)) return Results.BadRequest(new { error = "İlk kurulumda MeshCentral kimlik bilgisi gereklidir." });
            var now = DateTimeOffset.UtcNow;
            var record = new MeshCentralConnectorRecord(existing?.ConnectorId ?? Guid.NewGuid(), request.Name.Trim(), uri.GetLeftPart(UriPartial.Authority), request.Username.Trim(), mode, protectedCredential, string.IsNullOrWhiteSpace(request.MeshCtrlPath) ? existing?.MeshCtrlPath : Path.GetFullPath(request.MeshCtrlPath), request.Enabled, request.SyncIntervalMinutes, existing?.CreatedAtUtc ?? now, now, existing?.LastConnectionTestAtUtc, existing?.LastConnectionTestSucceeded, existing?.LastConnectionTestMessage, existing?.LastInventorySyncAtUtc, existing?.ServerVersion);
            var saved = await store.SaveMeshCentralConnectorAsync(record, ct).ConfigureAwait(false);
            return Results.Ok(ToMeshConnectorDto(saved));
        });

        groups.Operate.MapPost("/meshcentral/test", async (MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            if (connector is null) return Results.BadRequest(new { error = "Önce MeshCentral connector ayarlarını kaydedin." });
            var result = await connectorService.TestAsync(connector, ct).ConfigureAwait(false);
            var updated = connector with { LastConnectionTestAtUtc = DateTimeOffset.UtcNow, LastConnectionTestSucceeded = result.Succeeded, LastConnectionTestMessage = result.Message, ServerVersion = result.ServerVersion ?? connector.ServerVersion, MeshCtrlPath = result.MeshCtrlPath ?? connector.MeshCtrlPath, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await store.SaveMeshCentralConnectorAsync(updated, ct).ConfigureAwait(false);
            return Results.Ok(new MeshCentralConnectionTestDto(result.Succeeded, result.Message, result.ServerVersion, result.MeshCtrlPath));
        });

        groups.Operate.MapPost("/meshcentral/synchronize", async (MeshCentralConnectorService connectorService, IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            if (connector is null) return Results.BadRequest(new { error = "Önce MeshCentral connector ayarlarını kaydedin." });
            try
            {
                var devices = await connectorService.SynchronizeAsync(connector, ct).ConfigureAwait(false);
                return Results.Ok(new { synchronized = devices.Count, atUtc = DateTimeOffset.UtcNow });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Read.MapGet("/meshcentral/fleet", async (IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var devices = await store.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
            return Results.Ok(devices.Select(ToMeshDeviceDto));
        });

        groups.Read.MapGet("/meshcentral/fleet-summary", async (IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var x = await store.GetMeshCentralFleetSummaryAsync(ct).ConfigureAwait(false);
            return Results.Ok(new MeshCentralFleetSummaryDto(x.TotalDevices, x.OnlineDevices, x.MatchedAgents, x.MissingAgents, x.AmbiguousMatches, x.PendingDeployments, x.LastInventorySyncAtUtc));
        });

        groups.Read.MapGet("/meshcentral/deployments", async (int? limit, IMeshCentralFleetStore store, CancellationToken ct) =>
        {
            var rows = await store.GetMeshCentralDeploymentsAsync(limit ?? 200, ct).ConfigureAwait(false);
            return Results.Ok(rows.Select(ToMeshDeploymentDto));
        });

        groups.Operate.MapPost("/meshcentral/deploy", async (DeployMeshCentralAgentsRequest request, IMeshCentralFleetStore store, MeshCentralDeploymentHostedService deploymentWorker, CancellationToken ct) =>
        {
            var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            if (connector is null || !connector.Enabled) return Results.BadRequest(new { error = "MeshCentral connector yapılandırılmamış veya devre dışı." });
            var publicBase = Environment.GetEnvironmentVariable("YAZMABACKUP_PUBLIC_BASE_URI");
            if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
                return Results.BadRequest(new { error = "Tek tık dağıtım için YAZMABACKUP_PUBLIC_BASE_URI HTTPS adresi tanımlanmalıdır." });
            var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                return Results.BadRequest(new { error = "YAZMABACKUP_AGENT_PACKAGE_ZIP mevcut bir Agent ZIP paketini göstermelidir." });

            var inventory = await store.GetMeshCentralInventoryAsync(ct).ConfigureAwait(false);
            var nodeIds = (request.NodeIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(500).ToArray();
            if (nodeIds.Length == 0) return Results.BadRequest(new { error = "En az bir cihaz seçin." });

            var results = new List<MeshCentralDeploymentDto>();
            foreach (var nodeId in nodeIds)
            {
                var device = inventory.FirstOrDefault(x => x.NodeId == nodeId);
                if (device is null || !device.Online) continue;
                var now = DateTimeOffset.UtcNow;
                var deployment = await store.AddMeshCentralDeploymentAsync(new MeshCentralDeploymentRecord(
                    Guid.NewGuid(), connector.ConnectorId, device.NodeId, device.Name, device.MatchedAgentId,
                    "queued", now, now, "MeshCentral remote bootstrap accepted into background deployment queue.", null), ct).ConfigureAwait(false);
                deploymentWorker.Signal(deployment.DeploymentId);
                results.Add(ToMeshDeploymentDto(deployment));
            }

            if (results.Count == 0) return Results.BadRequest(new { error = "Seçilen cihazların hiçbiri çevrimiçi MeshCentral hedefi değil." });
            return Results.Accepted(value: results);
        });

        groups.Read.MapGet("/meshcentral-sync-events", async (int? limit, IProductionFabricStore store, CancellationToken ct) =>
        {
            var events = await store.GetMeshCentralSyncEventsAsync(Math.Clamp(limit ?? 200, 1, 5000), ct).ConfigureAwait(false);
            return Results.Ok(events.Select(x => new MeshCentralSyncEventDto(x.EventId, x.AgentId, x.NodeId, x.NodeStatus, x.DeploymentStatus, x.ReportedAtUtc, x.ReceivedAtUtc, x.CredentialId)));
        });

        groups.Security.MapPost("/agents/{agentId:guid}/meshcentral-link", async (Guid agentId, LinkMeshCentralNodeRequest request, IResilienceStore store, CancellationToken ct) =>
        {
            try
            {
                var link = await store.UpsertMeshCentralLinkAsync(agentId, request.MeshCentralBaseUri, request.NodeId, ct).ConfigureAwait(false);
                return Results.Ok(new MeshCentralLinkDto(link.AgentId, link.MeshCentralBaseUri, link.NodeId, link.LinkedAtUtc, link.LastSynchronizedAtUtc, link.LastKnownNodeStatus, link.LastDeploymentStatus));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        groups.Read.MapGet("/meshcentral-links", async (IResilienceStore store, CancellationToken ct) =>
        {
            var links = await store.GetMeshCentralLinksAsync(ct).ConfigureAwait(false);
            return Results.Ok(links.Select(x => new MeshCentralLinkDto(x.AgentId, x.MeshCentralBaseUri, x.NodeId, x.LinkedAtUtc, x.LastSynchronizedAtUtc, x.LastKnownNodeStatus, x.LastDeploymentStatus)));
        });

        return groups;
    }
}
