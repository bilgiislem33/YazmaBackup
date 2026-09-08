using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class AgentEndpointContractsTests
{
    private static readonly string[] ExpectedEnrollmentRoutes = ["/api/v1/agents/register"];
    private static readonly string[] ExpectedRuntimeRoutes =
    [
        "/api/v1/agent/heartbeat",
        "/api/v1/agent/transfer-telemetry"
    ];
    private static readonly string[] ExpectedCommandRoutes =
    [
        "/api/v1/agent/commands/next",
        "/api/v1/agent/commands/{commandId:guid}/lease",
        "/api/v1/agent/commands/{commandId:guid}/result"
    ];

    [Fact]
    public void Agent_routes_are_partitioned_by_security_responsibility()
    {
        Assert.Equal(ExpectedEnrollmentRoutes, AgentEndpointContracts.EnrollmentRoutes);
        Assert.Equal(ExpectedRuntimeRoutes, AgentEndpointContracts.RuntimeRoutes);
        Assert.Equal(ExpectedCommandRoutes, AgentEndpointContracts.CommandRoutes);

        var allRoutes = AgentEndpointContracts.EnrollmentRoutes
            .Concat(AgentEndpointContracts.RuntimeRoutes)
            .Concat(AgentEndpointContracts.CommandRoutes)
            .ToArray();

        Assert.Equal(allRoutes.Length, allRoutes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(allRoutes, route => Assert.StartsWith("/api/v1/", route, StringComparison.Ordinal));
    }
}
