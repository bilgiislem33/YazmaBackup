using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class AgentEndpointModulesTests
{
    [Fact]
    public void Agent_module_composition_registers_only_cutover_ready_enrollment_route()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterEnrollmentDependencies(builder.Services);
        var app = builder.Build();

        var result = app.MapAgentEndpointModules();

        Assert.Same(app, result);
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(static route => route is not null)
            .Select(static route => route!)
            .ToArray();

        Assert.Equal([AgentEndpointContracts.Register], routes);
    }

    [Fact]
    public void Individual_agent_modules_preserve_the_same_composition_root()
    {
        var builder = WebApplication.CreateBuilder();
        RegisterEnrollmentDependencies(builder.Services);
        var app = builder.Build();

        Assert.Same(app, app.MapAgentEnrollmentEndpoints());
        Assert.Same(app, app.MapAgentRuntimeEndpoints());
        Assert.Same(app, app.MapAgentCommandEndpoints());
    }

    private static void RegisterEnrollmentDependencies(IServiceCollection services)
    {
        services.AddSingleton<IControlPlaneStore>(_ => null!);
        services.AddSingleton<FleetAutopilotService>(_ => null!);
        services.AddSingleton<EnrollmentTicketService>(_ => null!);
    }
}
