namespace YazmaBackup.ControlPlane.Endpoints;

internal static class HealthEndpoints
{
    internal static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", (ControlPlaneHaRuntime ha) => Results.Ok(new
        {
            status = "live",
            nodeId = ha.NodeId,
            role = ha.Role,
            draining = ha.Draining
        }));

        endpoints.MapGet("/health/ready", (ControlPlaneHaRuntime ha) =>
            ha.ReadyForTraffic
                ? Results.Ok(new { status = "ready", nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining })
                : Results.Json(new { status = "standby", nodeId = ha.NodeId, role = ha.Role, draining = ha.Draining }, statusCode: StatusCodes.Status503ServiceUnavailable));

        endpoints.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            product = "YazmaBackup",
            version = "1.2.0",
            utc = DateTimeOffset.UtcNow
        }));

        return endpoints;
    }
}
