using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class RepositoryKeyEndpointTests
{
    [Fact]
    public void Repository_key_routes_remain_security_scoped_and_post_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        var app = builder.Build();

        var groups = app.CreateAdminEndpointGroups(string.Empty, false);
        groups.MapRepositoryKeyEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is
                "/api/v1/admin/agents/{agentId:guid}/repository-key" or
                "/api/v1/admin/agents/{agentId:guid}/repository-key/remove")
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, endpoint =>
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.Equal(["POST"], methods);
        });
    }
}
