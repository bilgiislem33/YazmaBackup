using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class ProductionFabricOrchestratorService(IControlPlaneStore controlStore, IProductionFabricStore fabricStore, ILogger<ProductionFabricOrchestratorService> logger) : BackgroundService
{
    private readonly string _nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var lease = await controlStore.TryAcquireOrRenewClusterLeaseAsync("production-fabric-orchestrator", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                {
                    var advances = await fabricStore.AdvanceProductionFabricAsync(now, stoppingToken).ConfigureAwait(false);
                    await EvaluateRunbooksAsync(now, stoppingToken).ConfigureAwait(false);
                    if (advances > 0) LogFabricAdvanced(logger, advances, _nodeId, lease.Epoch);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Production Fabric advanced {Count} state transition(s), node {NodeId}, epoch {Epoch}.")]
    private static partial void LogFabricAdvanced(ILogger logger, int count, string nodeId, long epoch);

    [LoggerMessage(Level = LogLevel.Error, Message = "Production Fabric orchestrator iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);

    private async Task EvaluateRunbooksAsync(DateTimeOffset now, CancellationToken ct)
    {
        var runs = await fabricStore.GetRecoveryRunbookRunsAsync(1000, ct).ConfigureAwait(false);
        foreach (var latest in runs.Where(x => x.CompletedAtUtc is not null).GroupBy(x => x.RunbookId).Select(g => g.OrderByDescending(x => x.StartedAtUtc).First()))
        {
            var fingerprint = $"recovery-runbook:{latest.RunbookId}:latest-run";
            if (latest.Status == "failed")
            {
                var failed = latest.Steps.Count(x => x.Status == "failed");
                await controlStore.UpsertAlarmAsync(fingerprint, AlarmSeverity.Critical, "recovery-runbook", "Recovery runbook başarısız", $"Run={latest.RunId}; başarısız adım={failed}; RTO bütçesi={latest.RtoBudgetMinutes} dk.", null, now, ct).ConfigureAwait(false);
            }
            else await controlStore.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
        }
    }
}
