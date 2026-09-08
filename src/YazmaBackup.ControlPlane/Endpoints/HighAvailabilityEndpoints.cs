using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

public static class HighAvailabilityEndpoints
{
    public static AdminEndpointGroups MapHighAvailabilityEndpoints(this AdminEndpointGroups groups, string stateEngine)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/state-engine/status", (TransactionalStateHealthService stateHealth) =>
        {
            try
            {
                return Results.Ok(stateHealth.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { engine = stateEngine, healthy = false, error = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        groups.Read.MapGet("/ha/status", (ControlPlaneHaRuntime ha, ControlPlaneDrInventoryService dr) =>
            Results.Ok(new
            {
                nodeId = ha.NodeId,
                role = ha.Role,
                haEnabled = ha.HaEnabled,
                draining = ha.Draining,
                readyForTraffic = ha.ReadyForTraffic,
                acceptsMutations = ha.AcceptsMutations,
                stateRoot = ha.StateRoot,
                dataProtectionMode = ha.CertificateProtected ? "certificate" : "dpapi-local-machine",
                dataProtectionCertificateThumbprint = ha.CertificateProtected ? ha.CertificateThumbprint : null,
                dr = dr.GetInventory()
            }));

        groups.Security.MapPost("/ha/drain", (ControlPlaneHaRuntime ha) =>
        {
            ha.SetDraining(true);
            return Results.Ok(new
            {
                nodeId = ha.NodeId,
                role = ha.Role,
                draining = ha.Draining,
                readyForTraffic = ha.ReadyForTraffic
            });
        });

        groups.Security.MapPost("/ha/undrain", (ControlPlaneHaRuntime ha) =>
        {
            if (ha.Role == "standby")
            {
                return Results.Conflict(new
                {
                    error = "Standby node cannot be undrained for production traffic. Promote the node by configuration and restart."
                });
            }

            ha.SetDraining(false);
            return Results.Ok(new
            {
                nodeId = ha.NodeId,
                role = ha.Role,
                draining = ha.Draining,
                readyForTraffic = ha.ReadyForTraffic
            });
        });

        groups.Read.MapGet("/dr/inventory", (ControlPlaneDrInventoryService dr) => Results.Ok(dr.GetInventory()));

        return groups;
    }
}
