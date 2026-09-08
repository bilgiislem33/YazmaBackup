using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class NotificationEndpoints
{
    internal static AdminEndpointGroups MapNotificationEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Security.MapPost("/notification-routes", async (CreateNotificationRouteRequest request, IDataProtectionProvider dataProtection, IResilienceStore store, CancellationToken ct) =>
        {
            if (!ValidText(request.Name, 128) || !Uri.TryCreate(request.Destination, UriKind.Absolute, out var destination) || destination.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(destination.UserInfo))
                return Results.BadRequest(new { error = "Notification route name or HTTPS destination is invalid." });
            var secret = request.HmacSecret ?? string.Empty;
            if (secret.Length is < 32 or > 256 || secret.Any(char.IsControl)) return Results.BadRequest(new { error = "HMAC secret must be 32..256 characters." });
            var allowedHosts = (Environment.GetEnvironmentVariable("YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!allowedHosts.Contains(destination.IdnHost, StringComparer.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Notification destination host is not allowlisted." });
            var severities = (request.Severities ?? [AlarmSeverity.Critical, AlarmSeverity.Warning]).Where(AlarmSeverity.All.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (severities.Length == 0) return Results.BadRequest(new { error = "At least one valid severity is required." });
            var protector = dataProtection.CreateProtector("YazmaBackup.NotificationHmac.v1");
            var route = new NotificationRouteRecord(Guid.NewGuid(), request.Name.Trim(), destination.ToString(), protector.Protect(secret), severities, request.Enabled, DateTimeOffset.UtcNow);
            secret = string.Empty;
            var created = await store.CreateNotificationRouteAsync(route, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/admin/notification-routes/{created.RouteId}", new NotificationRouteDto(created.RouteId, created.Name, created.Destination, created.Severities, created.Enabled, created.CreatedAtUtc));
        });

        groups.Security.MapGet("/notification-routes", async (IResilienceStore store, CancellationToken ct) =>
        {
            var routes = await store.GetNotificationRoutesAsync(ct).ConfigureAwait(false);
            return Results.Ok(routes.Select(x => new NotificationRouteDto(x.RouteId, x.Name, x.Destination, x.Severities, x.Enabled, x.CreatedAtUtc)));
        });

        groups.Security.MapDelete("/notification-routes/{routeId:guid}", async (Guid routeId, IResilienceStore store, CancellationToken ct) =>
            await store.DeleteNotificationRouteAsync(routeId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

        groups.Security.MapGet("/notification-deliveries", async (int? limit, IResilienceStore store, CancellationToken ct) =>
        {
            var deliveries = await store.GetNotificationDeliveriesAsync(Math.Clamp(limit ?? 200, 1, 1000), ct).ConfigureAwait(false);
            return Results.Ok(deliveries.Select(x => new NotificationDeliveryDto(x.DeliveryId, x.RouteId, x.AlarmId, x.AlarmVersionUtc, x.CreatedAtUtc, x.CompletedAtUtc, x.AttemptCount, x.Succeeded, x.Error)));
        });

        return groups;
    }
}
