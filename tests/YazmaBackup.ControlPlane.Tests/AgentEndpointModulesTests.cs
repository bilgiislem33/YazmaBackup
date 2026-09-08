using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class AgentEndpointModulesTests
{
    [Fact]
    public void Agent_module_composition_registers_cutover_ready_enrollment_and_runtime_routes()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterAgentDependencies(builder.Services);
        var app = builder.Build();

        var result = app.MapAgentEndpointModules();

        Assert.Same(app, result);
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(static route => route is not null)
            .Select(static route => route!)
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();

        var expected = AgentEndpointContracts.EnrollmentRoutes
            .Concat(AgentEndpointContracts.RuntimeRoutes)
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, routes);
    }

    [Fact]
    public void Individual_agent_modules_preserve_the_same_composition_root()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterAgentDependencies(builder.Services);
        var app = builder.Build();

        Assert.Same(app, app.MapAgentEnrollmentEndpoints());
        Assert.Same(app, app.MapAgentRuntimeEndpoints());
        Assert.Same(app, app.MapAgentCommandEndpoints());
    }

    private static void RegisterAgentDependencies(IServiceCollection services)
    {
        services.AddSingleton<IControlPlaneStore>(_ => null!);
        services.AddSingleton<IMeshCentralFleetStore>(_ => null!);
        services.AddSingleton<MeshCentralBootstrapTicketService>(_ => null!);
        services.AddSingleton<GlobalNasProfileService>(_ => null!);
        services.AddSingleton<TransferTelemetryRegistry>(_ => null!);
    }
}
