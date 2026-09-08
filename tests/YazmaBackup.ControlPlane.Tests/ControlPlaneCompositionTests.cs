using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.ControlPlane.Endpoints;
using YazmaBackup.ControlPlane.Hosting;

namespace YazmaBackup.ControlPlane.Tests;

[Collection("ControlPlane environment")]
public sealed class ControlPlaneCompositionTests
{
    [Fact]
    public async Task Production_composition_preserves_every_route_and_method_without_duplicates()
    {
        using var scope = new StateStoreTestScope();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scope.Root,
            EnvironmentName = "Development"
        });
        var settings = builder.AddControlPlaneServices();
        await using var app = builder.Build();
        app.MapControlPlaneEndpoints(settings);
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText != "{*path:nonfile}")
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                    .Select(method => method + " " + endpoint.RoutePattern.RawText))
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "control-plane-routes.txt"));

        Assert.Equal(expected.Length, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, actual);
    }
}
