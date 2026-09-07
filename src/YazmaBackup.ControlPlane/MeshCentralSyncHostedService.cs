namespace YazmaBackup.ControlPlane;

public sealed class MeshCentralSyncHostedService(IServiceProvider services, ILogger<MeshCentralSyncHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogScheduledSynchronizationFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1202, nameof(LogScheduledSynchronizationFailed)),
        "MeshCentral scheduled synchronization failed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delayMinutes = 5;
            try
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IMeshCentralFleetStore>();
                var connector = await store.GetMeshCentralConnectorAsync(stoppingToken).ConfigureAwait(false);
                if (connector is not null)
                {
                    delayMinutes = Math.Clamp(connector.SyncIntervalMinutes, 1, 1440);
                    if (connector.Enabled)
                    {
                        var connectorService = scope.ServiceProvider.GetRequiredService<MeshCentralConnectorService>();
                        await connectorService.SynchronizeAsync(connector, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogScheduledSynchronizationFailed(logger, ex); }
            await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken).ConfigureAwait(false);
        }
    }
}
