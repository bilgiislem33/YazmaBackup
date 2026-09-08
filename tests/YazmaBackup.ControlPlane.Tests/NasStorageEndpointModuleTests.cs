using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class NasStorageEndpointModuleTests
{
    [Fact]
    public void NAS_storage_routes_preserve_permission_and_method_partition()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<GlobalNasProfileStore>(_ => null!);
        builder.Services.AddSingleton<GlobalNasProfileService>(_ => null!);
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        var app = builder.Build();

        var groups = app.CreateAdminEndpointGroups(string.Empty, false);
        groups.MapNasStorageEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Route = endpoint.RoutePattern.RawText,
                Methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []
            })
            .Where(item => item.Route is not null && item.Route.StartsWith("/api/v1/admin/", StringComparison.Ordinal))
            .SelectMany(item => item.Methods.Select(method => $"{method} {item.Route}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "GET /api/v1/admin/nas/global-profile",
            "POST /api/v1/admin/agents/{agentId:guid}/nas-access-test",
            "POST /api/v1/admin/agents/{agentId:guid}/nas-credential",
            "POST /api/v1/admin/agents/{agentId:guid}/nas-credential/save-and-test",
            "POST /api/v1/admin/nas/global-profile"
        ],
        routes);
    }

    [Fact]
    public void NAS_route_contracts_are_unique_within_each_permission_partition()
    {
        Assert.Equal(
            NasStorageEndpointContracts.ReadRoutes.Count,
            NasStorageEndpointContracts.ReadRoutes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            NasStorageEndpointContracts.OperateRoutes.Count,
            NasStorageEndpointContracts.OperateRoutes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            NasStorageEndpointContracts.SecurityRoutes.Count,
            NasStorageEndpointContracts.SecurityRoutes.Distinct(StringComparer.Ordinal).Count());
    }
}
