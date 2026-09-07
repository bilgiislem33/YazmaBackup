using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class OperationalSentinelService(IControlPlaneStore store, ILogger<OperationalSentinelService> logger) : BackgroundService
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
                var lease = await store.TryAcquireOrRenewClusterLeaseAsync("operational-sentinel", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                    await EvaluateAsync(now, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Operational sentinel iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);

    private async Task EvaluateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var commands = await store.GetRecentCommandsAsync(1000, ct).ConfigureAwait(false);
        var onlineCutoff = now.AddMinutes(-5);

        foreach (var agent in agents)
        {
            var protectionFingerprint = $"agent:{agent.AgentId}:protection-lock";
            if (string.Equals(agent.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase))
                await store.UpsertAlarmAsync(protectionFingerprint, AlarmSeverity.Critical, "protection", $"{agent.MachineName} koruma kilidinde", agent.ProtectionReason, agent.AgentId, now, ct).ConfigureAwait(false);
            else
                await store.ResolveAlarmAsync(protectionFingerprint, now, ct).ConfigureAwait(false);

            var offlineFingerprint = $"agent:{agent.AgentId}:offline";
            if (agent.LastSeenUtc < onlineCutoff)
                await store.UpsertAlarmAsync(offlineFingerprint, AlarmSeverity.Warning, "availability", $"{agent.MachineName} çevrimdışı", $"Son heartbeat: {agent.LastSeenUtc:O}", agent.AgentId, now, ct).ConfigureAwait(false);
            else
                await store.ResolveAlarmAsync(offlineFingerprint, now, ct).ConfigureAwait(false);

            foreach (var circuit in agent.RepositoryCircuits ?? [])
            {
                var fingerprint = $"agent:{agent.AgentId}:repository-circuit:{circuit.RepositoryId}";
                if (circuit.IsOpen(now))
                {
                    var severity = circuit.ConsecutiveFailures >= 6 ? AlarmSeverity.Critical : AlarmSeverity.Warning;
                    await store.UpsertAlarmAsync(fingerprint, severity, "repository-circuit", $"{agent.MachineName}: repository erişimi geri çekildi", $"Repository={circuit.RepositoryId}; failures={circuit.ConsecutiveFailures}; retry={circuit.OpenUntilUtc:O}; error={circuit.LastError}", agent.AgentId, now, ct).ConfigureAwait(false);
                }
                else
                    await store.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
            }

            await EvaluateLatestCommandAsync(agent, AgentCommandType.BackupPath, "backup", AlarmSeverity.Warning, commands, now, ct).ConfigureAwait(false);
            await EvaluateLatestCommandAsync(agent, AgentCommandType.RestoreDrill, "restore-drill", AlarmSeverity.Critical, commands, now, ct).ConfigureAwait(false);
        }

        foreach (var policy in policies.Where(p => p.Enabled))
        {
            var fingerprint = $"policy:{policy.PolicyId}:overdue";
            var tolerance = TimeSpan.FromMinutes(Math.Max(10, policy.IntervalMinutes));
            if (policy.NextRunAtUtc < now.Subtract(tolerance))
                await store.UpsertAlarmAsync(fingerprint, AlarmSeverity.Warning, "scheduler", $"{policy.Name} politikası gecikmiş", $"Beklenen çalışma: {policy.NextRunAtUtc:O}", policy.AgentId, now, ct).ConfigureAwait(false);
            else
                await store.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
        }
    }

    private async Task EvaluateLatestCommandAsync(AgentRecord agent, AgentCommandType type, string category, string severity, IReadOnlyList<AgentCommand> commands, DateTimeOffset now, CancellationToken ct)
    {
        var fingerprint = $"agent:{agent.AgentId}:{category}-failed";
        var latest = commands.Where(c => c.AgentId == agent.AgentId && c.Type == type && c.CompletedAtUtc is not null)
            .OrderByDescending(c => c.CompletedAtUtc).FirstOrDefault();
        if (latest is not null && !latest.Succeeded)
            await store.UpsertAlarmAsync(fingerprint, severity, category, $"{agent.MachineName}: son {category} işlemi başarısız", latest.Error, agent.AgentId, now, ct).ConfigureAwait(false);
        else if (latest is not null && latest.Succeeded)
            await store.ResolveAlarmAsync(fingerprint, now, ct).ConfigureAwait(false);
    }
}
