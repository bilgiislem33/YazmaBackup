using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

public static class ControlPlaneEndpointModules
{
    public static IEndpointRouteBuilder MapPublicEndpointModules(
        this IEndpointRouteBuilder endpoints,
        bool allowInsecureUiCookie)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthEndpoints();
        endpoints.MapSessionEndpoints(allowInsecureUiCookie);
        return endpoints;
    }
}
