using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

/// <summary>
/// Composition boundary for the agent-facing API. The current Program.cs handlers can be
/// migrated into these modules one responsibility at a time without changing their public
/// routes or security semantics.
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

    internal static IEndpointRouteBuilder MapAgentEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }

    internal static IEndpointRouteBuilder MapAgentRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }

    internal static IEndpointRouteBuilder MapAgentCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
