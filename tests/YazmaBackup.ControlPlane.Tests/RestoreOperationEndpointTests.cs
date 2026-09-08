using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class RestoreOperationEndpointTests
{
    private static readonly string[] ExpectedRoutes =
    [
        "/api/v1/admin/agents/{agentId:guid}/restore",
        "/api/v1/admin/agents/{agentId:guid}/restore-entries",
        "/api/v1/admin/agents/{agentId:guid}/restore-point-in-time",
        "/api/v1/admin/agents/{agentId:guid}/restore-points",
        "/api/v1/admin/agents/{agentId:guid}/restore-sandbox"
    ];

    [Fact]
    public void Restore_routes_are_operate_scoped_and_post_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        var app = builder.Build();

        var groups = app.CreateAdminEndpointGroups(string.Empty, false);
        groups.MapRestoreOperationEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/restore", StringComparison.Ordinal) == true)
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedRoutes, endpoints.Select(endpoint => endpoint.RoutePattern.RawText).ToArray());
        Assert.All(endpoints, endpoint =>
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.Equal(["POST"], methods);
        });
    }
}
