using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Metadata;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class NasStorageEndpointModuleTests
{
    [Fact]
    public void Global_NAS_profile_routes_preserve_read_and_security_method_split()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<GlobalNasProfileStore>(_ => null!);
        builder.Services.AddSingleton<GlobalNasProfileService>(_ => null!);
        var app = builder.Build();

        var groups = app.CreateAdminEndpointGroups(string.Empty, false);
        groups.MapNasStorageEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                "/api/v1/admin/nas/global-profile",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, endpoints.Length);

        var methods = endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .OrderBy(method => method, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GET", "POST"], methods);
    }
}
