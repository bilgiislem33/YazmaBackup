using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class NotificationDispatcherService(
    IControlPlaneStore controlStore,
    IResilienceStore resilienceStore,
    HttpClient httpClient,
    IDataProtectionProvider dataProtection,
    ILogger<NotificationDispatcherService> logger) : BackgroundService
{
    private readonly string _nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
    private readonly IDataProtector _protector = dataProtection.CreateProtector("YazmaBackup.NotificationHmac.v1");
    private readonly HashSet<string> _allowedHosts = ParseAllowedHosts(Environment.GetEnvironmentVariable("YAZMABACKUP_NOTIFICATION_ALLOWED_HOSTS"));
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var lease = await controlStore.TryAcquireOrRenewClusterLeaseAsync("notification-dispatcher", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                    await DispatchAsync(now, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification dispatcher iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);

    private async Task DispatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var deliveries = await resilienceStore.PrepareNotificationDeliveriesAsync(now, ct).ConfigureAwait(false);
        if (deliveries.Count == 0) return;
        var routes = (await resilienceStore.GetNotificationRoutesAsync(ct).ConfigureAwait(false)).ToDictionary(x => x.RouteId);
        var alarms = (await controlStore.GetAlarmsAsync(5000, includeResolved: true, ct).ConfigureAwait(false)).ToDictionary(x => x.AlarmId);

        foreach (var delivery in deliveries)
        {
            ct.ThrowIfCancellationRequested();
            if (!routes.TryGetValue(delivery.RouteId, out var route) || !alarms.TryGetValue(delivery.AlarmId, out var alarm))
            {
                await resilienceStore.CompleteNotificationDeliveryAsync(delivery.DeliveryId, false, "Route or alarm no longer exists.", now, ct).ConfigureAwait(false);
                continue;
            }
            try
            {
                ValidateDestination(route.Destination);
                var secret = _protector.Unprotect(route.ProtectedHmacSecret);
                try
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        deliveryId = delivery.DeliveryId,
                        product = "YazmaBackup",
                        alarm = new { alarm.AlarmId, alarm.Severity, alarm.Category, alarm.Title, alarm.Details, alarm.Status, alarm.AgentId, alarm.FirstSeenAtUtc, alarm.LastSeenAtUtc }
                    });
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var signature = ComputeSignature(secret, timestamp, body);
                    using var request = new HttpRequestMessage(HttpMethod.Post, route.Destination);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    request.Headers.Add("X-YazmaBackup-Delivery", delivery.DeliveryId.ToString("D"));
                    request.Headers.Add("X-YazmaBackup-Timestamp", timestamp);
                    request.Headers.Add("X-YazmaBackup-Signature", "sha256=" + signature);
                    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("YazmaBackup", "1.2.0"));
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    var ok = response.IsSuccessStatusCode;
                    await resilienceStore.CompleteNotificationDeliveryAsync(delivery.DeliveryId, ok, ok ? null : $"HTTP {(int)response.StatusCode}", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                }
                finally { secret = string.Empty; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await resilienceStore.CompleteNotificationDeliveryAsync(delivery.DeliveryId, false, ex.Message, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
        }
    }

    private void ValidateDestination(string destination)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Notification destination must be an absolute HTTPS URI without userinfo.");
        if (_allowedHosts.Count == 0 || !_allowedHosts.Contains(uri.IdnHost))
            throw new InvalidOperationException("Notification destination host is not allowlisted.");
    }

    private static HashSet<string> ParseAllowedHosts(string? value) =>
        new((value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

    private static string ComputeSignature(string secret, string timestamp, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
