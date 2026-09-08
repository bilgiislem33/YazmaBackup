using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

public static class ControlPlaneEndpointModules
{
    internal static void MapControlPlaneEndpoints(this WebApplication app, Hosting.ControlPlaneHostSettings settings)
    {
        app.MapPublicEndpointModules(settings.AllowInsecureUiCookie);
        app.MapAgentEndpointModules();
        app.CreateAdminEndpointGroups(settings.AdminKey, settings.LegacyAdminKeyEnabled)
            .MapAdminEndpointModules(settings.StateEngine);
        app.MapFallbackToFile("index.html");
    }

    public static IEndpointRouteBuilder MapPublicEndpointModules(
        this IEndpointRouteBuilder endpoints,
        bool allowInsecureUiCookie)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthEndpoints();
        endpoints.MapSessionEndpoints(allowInsecureUiCookie);
        endpoints.MapMeshCentralPublicEndpoints();
        return endpoints;
    }
}
