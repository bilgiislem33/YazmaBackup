using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class HealthEndpointModuleTests
{
    private static readonly string[] ExpectedRoutes = ["/health", "/health/live", "/health/ready"];

    [Fact]
    public void Health_module_registers_only_expected_public_health_routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new ControlPlaneHaRuntime(Path.GetTempPath(), "single", "test-node", string.Empty));
        var app = builder.Build();

        app.MapHealthEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(static route => route is not null)
            .Select(static route => route!)
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedRoutes, routes);
    }
}
