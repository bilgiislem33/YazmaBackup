using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class StorageRestoreModuleContractTests
{
    [Fact]
    public void Repository_key_routes_are_security_admin_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        var app = builder.Build();
        var groups = app.CreateAdminEndpointGroups(string.Empty, false);

        groups.MapRepositoryKeyEndpoints();

        var routes = Routes(app);
        Assert.Equal(2, routes.Length);
        Assert.Contains(routes, x => x.Route == "/api/v1/admin/agents/{agentId:guid}/repository-key" && x.Method == "POST");
        Assert.Contains(routes, x => x.Route == "/api/v1/admin/agents/{agentId:guid}/repository-key/remove" && x.Method == "POST");
    }

    [Fact]
    public void Restore_operation_family_exposes_five_operational_routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        var app = builder.Build();
        var groups = app.CreateAdminEndpointGroups(string.Empty, false);

        groups.MapRestoreOperationEndpoints();

        var routes = Routes(app);
        Assert.Equal(5, routes.Length);
        Assert.All(routes, x => Assert.Equal("POST", x.Method));
        Assert.Contains(routes, x => x.Route.EndsWith("/restore-points", StringComparison.Ordinal));
        Assert.Contains(routes, x => x.Route.EndsWith("/restore-entries", StringComparison.Ordinal));
        Assert.Contains(routes, x => x.Route.EndsWith("/restore", StringComparison.Ordinal));
        Assert.Contains(routes, x => x.Route.EndsWith("/restore-point-in-time", StringComparison.Ordinal));
        Assert.Contains(routes, x => x.Route.EndsWith("/restore-sandbox", StringComparison.Ordinal));
    }

    private static (string Route, string Method)[] Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (endpoint.RoutePattern.RawText ?? string.Empty, method)))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ThenBy(x => x.method, StringComparer.Ordinal)
            .Select(x => (Route: x.Item1, Method: x.method))
            .ToArray();
}
