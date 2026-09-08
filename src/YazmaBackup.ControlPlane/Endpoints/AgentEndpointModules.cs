using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

/// <summary>
/// Composition boundary for the agent-facing API. Handlers are migrated into these modules
/// one responsibility at a time while preserving their public routes and security semantics.
/// </summary>
internal static class AgentEndpointModules
{
    internal static IEndpointRouteBuilder MapAgentEndpointModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapAgentEnrollmentEndpoints();
        endpoints.MapAgentRuntimeEndpoints();
        endpoints.MapAgentCommandEndpoints();
        return endpoints;
    }
}
