using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class ResilienceOrchestratorService(IControlPlaneStore controlStore, IResilienceStore resilienceStore, ILogger<ResilienceOrchestratorService> logger) : BackgroundService
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
                var lease = await controlStore.TryAcquireOrRenewClusterLeaseAsync("resilience-orchestrator", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                {
                    var count = await resilienceStore.EnqueueDueResilienceAsync(now, stoppingToken).ConfigureAwait(false);
                    await EvaluateRepositoryHealthAsync(now, stoppingToken).ConfigureAwait(false);
                    await EvaluateRecoveryPlansAsync(now, stoppingToken).ConfigureAwait(false);
                    if (count > 0) LogResilienceQueued(logger, count, _nodeId, lease.Epoch);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resilience orchestrator queued {Count} command(s), node {NodeId}, epoch {Epoch}.")]
    private static partial void LogResilienceQueued(ILogger logger, int count, string nodeId, long epoch);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resilience orchestrator iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);

    private async Task EvaluateRepositoryHealthAsync(DateTimeOffset now, CancellationToken ct)
    {
        var records = await resilienceStore.GetRepositoryHealthAsync(5000, ct).ConfigureAwait(false);
        foreach (var latest in records.GroupBy(x => x.PolicyId).Select(g => g.OrderByDescending(x => x.MeasuredAtUtc).First()))
        {
            var fingerprint = $"policy:{latest.PolicyId}:repository-health";
            if (latest.Status == RepositoryHealthStatus.Critical)
                await controlStore.UpsertAlarmAsync(fingerprint, AlarmSeverity.Critical, "repository-health", $"Repository kritik: {latest.RepositoryId}", latest.Reason, latest.AgentId, now, ct).ConfigureAwait(false);
            else if (latest.Status == RepositoryHealthStatus.Warning)
                await controlStore.UpsertAlarmAsync(fingerprint, AlarmSeverity.Warning, "repository-health", $"Repository uyarısı: {latest.RepositoryId}", latest.Reason, latest.AgentId, now, ct).ConfigureAwait(false);
            else
                await controlStore.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
        }
    }

    private async Task EvaluateRecoveryPlansAsync(DateTimeOffset now, CancellationToken ct)
    {
        var runs = await resilienceStore.GetRecoveryRunsAsync(1000, ct).ConfigureAwait(false);
        foreach (var latest in runs.Where(x => x.CompletedAtUtc is not null).GroupBy(x => x.PlanId).Select(g => g.OrderByDescending(x => x.StartedAtUtc).First()))
        {
            var fingerprint = $"recovery-plan:{latest.PlanId}:latest-run";
            if (latest.Status == "failed")
            {
                var failed = latest.Targets.Count(x => x.Succeeded != true);
                var details = $"Run {latest.RunId}; başarısız hedef={failed}; doğrulanan byte={latest.VerifiedBytes}; en uzun restore={latest.LongestRestoreMilliseconds} ms; RTO={latest.RtoTargetMinutes} dk.";
                await controlStore.UpsertAlarmAsync(fingerprint, AlarmSeverity.Critical, "recovery-plan", "Disaster recovery tatbikatı başarısız", details, null, now, ct).ConfigureAwait(false);
            }
            else
                await controlStore.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
        }
    }
}
