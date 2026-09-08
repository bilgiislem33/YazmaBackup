using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class HealthEndpointModuleTests
{
    [Fact]
    public void Health_module_registers_only_expected_public_health_routes()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapHealthEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route is not null)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["/health", "/health/live", "/health/ready"], routes);
    }
}
