using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YazmaBackup.ControlPlane.Hosting;

namespace YazmaBackup.ControlPlane.Tests;

[Collection("ControlPlane environment")]
public sealed class AutonomousExecutionPolicyTests
{
    [Fact]
    public void Autonomous_executor_is_fail_safe_and_disabled_by_default()
    {
        using var scope = new StateStoreTestScope();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scope.Root,
            EnvironmentName = "Development"
        });

        var settings = builder.AddControlPlaneServices();
        var policy = Assert.IsType<AutonomousExecutionPolicy>(builder.Services
            .Single(x => x.ServiceType == typeof(AutonomousExecutionPolicy)).ImplementationInstance);

        Assert.False(settings.AutonomousExecutionEnabled);
        Assert.False(policy.Enabled);
        var defaultWorkers = builder.Services.Count(x => x.ServiceType == typeof(IHostedService));

        Environment.SetEnvironmentVariable(AutonomousExecutionPolicy.EnvironmentVariable, "true");
        var enabledBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scope.Root,
            EnvironmentName = "Development"
        });
        var enabledSettings = enabledBuilder.AddControlPlaneServices();
        var enabledPolicy = Assert.IsType<AutonomousExecutionPolicy>(enabledBuilder.Services
            .Single(x => x.ServiceType == typeof(AutonomousExecutionPolicy)).ImplementationInstance);

        Assert.True(enabledSettings.AutonomousExecutionEnabled);
        Assert.True(enabledPolicy.Enabled);
        Assert.Equal(defaultWorkers + 1, enabledBuilder.Services.Count(x => x.ServiceType == typeof(IHostedService)));
    }
}
