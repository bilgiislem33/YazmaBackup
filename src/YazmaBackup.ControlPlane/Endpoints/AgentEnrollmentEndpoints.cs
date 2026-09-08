using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AgentEnrollmentEndpoints
{
    internal static IEndpointRouteBuilder MapAgentEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(AgentEndpointContracts.Register, async (
            HttpContext http,
            RegisterAgentRequest request,
            IControlPlaneStore store,
            IMeshCentralFleetStore fleet,
            MeshCentralBootstrapTicketService tickets,
            CancellationToken ct) =>
        {
            if (!AgentRequestSecurity.ValidMachine(request.MachineName) ||
                !AgentRequestSecurity.ValidText(request.OperatingSystem, 256) ||
                !AgentRequestSecurity.ValidText(request.AgentVersion, 64) ||
                (request.Capabilities?.Count ?? 0) > 64 ||
                !AgentRequestSecurity.ValidOptionalPublicKey(request.KeyExchangePublicKeyPem))
            {
                return Results.BadRequest(new { error = "Invalid agent registration payload." });
            }

            var enrollmentToken = http.Request.Headers["X-YazmaBackup-Enrollment"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(enrollmentToken)) return Results.Unauthorized();

            try
            {
                var (agent, token) = await store.EnrollAgentAsync(
                    enrollmentToken,
                    request.MachineName.Trim(),
                    request.OperatingSystem.Trim(),
                    request.AgentVersion.Trim(),
                    AgentRequestSecurity.SanitizeCapabilities(request.Capabilities),
                    request.KeyExchangePublicKeyPem?.Trim(),
                    ct).ConfigureAwait(false);

                if (tickets.TryConsumeEnrollment(enrollmentToken, out var correlation) && correlation is not null)
                {
                    var deployment = (await fleet.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false))
                        .FirstOrDefault(x => x.DeploymentId == correlation.DeploymentId && x.NodeId == correlation.NodeId);

                    if (deployment is not null && deployment.Status is ("dispatching" or "dispatched" or "installing"))
                    {
                        await fleet.UpdateMeshCentralDeploymentAsync(
                            deployment.DeploymentId,
                            "installing",
                            $"Agent registration bound to exact MeshCentral deployment; waiting authenticated heartbeat. Machine={request.MachineName.Trim()}",
                            deployment.CommandId,
                            agent.AgentId,
                            ct).ConfigureAwait(false);
                    }
                }

                return Results.Ok(new RegisterAgentResponse(agent.AgentId, token));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
        });

        return endpoints;
    }
}
