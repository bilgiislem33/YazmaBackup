using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class AgentEndpointModulesTests
{
    [Fact]
    public void Agent_module_composition_is_safe_before_handler_cutover()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var result = app.MapAgentEndpointModules();

        Assert.Same(app, result);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
    }

    [Fact]
    public void Individual_agent_modules_preserve_the_same_composition_root()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        Assert.Same(app, app.MapAgentEnrollmentEndpoints());
        Assert.Same(app, app.MapAgentRuntimeEndpoints());
        Assert.Same(app, app.MapAgentCommandEndpoints());
    }
}
