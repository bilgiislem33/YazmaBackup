using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class BackupOperationEndpointTests
{
    [Fact]
    public void Backup_operation_family_preserves_operate_and_backup_admin_split()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IControlPlaneStore>(_ => null!);
        builder.Services.AddSingleton<RepositoryKeyVault>(_ => null!);
        var app = builder.Build();
        var groups = app.CreateAdminEndpointGroups(string.Empty, false);

        groups.MapBackupOperationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                    .Select(method => (Route: endpoint.RoutePattern.RawText ?? string.Empty, Method: method)))
            .OrderBy(x => x.Route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(4, routes.Length);
        Assert.All(routes, route => Assert.Equal("POST", route.Method));
        Assert.Contains(routes, route => route.Route.EndsWith("/backup", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Route.EndsWith("/scrub", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Route.EndsWith("/restore-drill", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Route.EndsWith("/repository-health", StringComparison.Ordinal));
    }
}
