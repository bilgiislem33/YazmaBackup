using System.Collections.Concurrent;
using System.Threading.Channels;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed class MeshCentralDeploymentHostedService(IServiceProvider services, ILogger<MeshCentralDeploymentHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> LogDeploymentFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1203, nameof(LogDeploymentFailed)),
        "MeshCentral deployment worker failed for {DeploymentId}");

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public bool Signal(Guid deploymentId)
    {
        if (!_scheduled.TryAdd(deploymentId, 0)) return false;
        if (_queue.Writer.TryWrite(deploymentId)) return true;
        _scheduled.TryRemove(deploymentId, out _);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverQueuedAsync(stoppingToken).ConfigureAwait(false);
        var nextRecovery = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var deploymentId))
            {
                try { await DispatchAsync(deploymentId, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { LogDeploymentFailed(logger, deploymentId, ex); }
                finally { _scheduled.TryRemove(deploymentId, out _); }
            }

            if (DateTimeOffset.UtcNow >= nextRecovery)
            {
                await RecoverQueuedAsync(stoppingToken).ConfigureAwait(false);
                nextRecovery = DateTimeOffset.UtcNow.AddSeconds(10);
            }

            var waitForData = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var delay = Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            await Task.WhenAny(waitForData, delay).ConfigureAwait(false);
        }
    }

    private async Task RecoverQueuedAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMeshCentralFleetStore>();
        var deployments = await store.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false);
        foreach (var deployment in deployments.Where(x => x.Status == "queued").OrderBy(x => x.RequestedAtUtc))
            Signal(deployment.DeploymentId);

        var staleCutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var deployment in deployments.Where(x => x.Status == "dispatching" && x.UpdatedAtUtc < staleCutoff))
            await store.UpdateMeshCentralDeploymentAsync(
                deployment.DeploymentId,
                "installing",
                "MeshCentral komutu gönderildikten sonra worker yeniden başladı veya yanıt gecikti. Yinelenen kurulum başlatılmadı; exact Agent kaydı/heartbeat 30 dakikalık deployment penceresi içinde bekleniyor.",
                deployment.CommandId,
                deployment.AgentId,
                ct).ConfigureAwait(false);

        // R5.8 fallback: while any deployment is waiting for heartbeat reconciliation, refresh
        // MeshCentral inventory every recovery cycle instead of waiting for the normal 1-5 minute sync cadence.
        if (deployments.Any(x => x.Status == "installing"))
        {
            var connector = await store.GetMeshCentralConnectorAsync(ct).ConfigureAwait(false);
            if (connector is not null && connector.Enabled)
            {
                var connectorService = scope.ServiceProvider.GetRequiredService<MeshCentralConnectorService>();
                await connectorService.SynchronizeAsync(connector, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(Guid deploymentId, CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMeshCentralFleetStore>();
        var connectorService = scope.ServiceProvider.GetRequiredService<MeshCentralConnectorService>();
        var tickets = scope.ServiceProvider.GetRequiredService<MeshCentralBootstrapTicketService>();

        var deployment = (await store.GetMeshCentralDeploymentsAsync(5000, stoppingToken).ConfigureAwait(false))
            .FirstOrDefault(x => x.DeploymentId == deploymentId);
        if (deployment is null || deployment.Status != "queued") return;

        var connector = await store.GetMeshCentralConnectorAsync(stoppingToken).ConfigureAwait(false);
        if (connector is null || !connector.Enabled)
        {
            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "failed", "MeshCentral connector is missing or disabled.", null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
            return;
        }

        var device = (await store.GetMeshCentralInventoryAsync(stoppingToken).ConfigureAwait(false))
            .FirstOrDefault(x => x.NodeId == deployment.NodeId);
        if (device is null || !device.Online)
        {
            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "failed", "MeshCentral target is missing or offline.", null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
            return;
        }

        var publicBase = Environment.GetEnvironmentVariable("YAZMABACKUP_PUBLIC_BASE_URI");
        var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
        {
            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "failed", "YAZMABACKUP_PUBLIC_BASE_URI must be a valid HTTPS address.", null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "failed", "YAZMABACKUP_AGENT_PACKAGE_ZIP does not point to an existing Agent package.", null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
            return;
        }

        var ticket = tickets.IssueBootstrap(device.NodeId, deploymentId, TimeSpan.FromMinutes(10));
        var bootstrapUrl = new Uri(publicUri, "/api/v1/bootstrap/meshcentral/" + Uri.EscapeDataString(ticket.Token)).ToString();
        var escapedUrl = bootstrapUrl.Replace("'", "''", StringComparison.Ordinal);
        const string successMarker = "YAZMABACKUP_DEPLOYMENT_SUCCESS";
        const string errorMarker = "YAZMABACKUP_DEPLOYMENT_ERROR:";
        var script = "$ErrorActionPreference='Stop';try{$r=Invoke-RestMethod -UseBasicParsing -Uri '" + escapedUrl + "';Invoke-Expression $r.script;Write-Output '" + successMarker + "'}catch{Write-Output ('" + errorMarker + "'+$_.Exception.Message);exit 71}";

        await store.UpdateMeshCentralDeploymentAsync(deploymentId, "dispatching", "Background worker is dispatching the MeshCentral bootstrap command.", null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var run = await connectorService.RunPowerShellAsync(connector, device.NodeId, script, timeout.Token).ConfigureAwait(false);
            if (!run.Output.Contains(successMarker, StringComparison.Ordinal))
                throw new InvalidOperationException("MeshCentral command returned without YazmaBackup deployment success evidence: " + SafeDetail(run.Output));

            var current = (await store.GetMeshCentralDeploymentsAsync(5000, stoppingToken).ConfigureAwait(false))
                .FirstOrDefault(x => x.DeploymentId == deploymentId);
            if (current is not null && current.Status == "succeeded")
            {
                await store.UpdateMeshCentralDeploymentAsync(deploymentId, "succeeded", current.Detail, run.CommandId, current.AgentId, stoppingToken).ConfigureAwait(false);
                return;
            }

            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "installing", "Remote installer, service and local enrollment evidence completed; waiting for fleet heartbeat match.", run.CommandId, current?.AgentId ?? deployment.AgentId, stoppingToken).ConfigureAwait(false);
            await connectorService.SynchronizeAsync(connector, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // R6.7: meshctrl --reply waits for the remote command response. A transport
            // wait timeout is not proof that the already-dispatched installer failed.
            // Keep exact deployment correlation alive and let authenticated registration
            // / heartbeat complete the deployment inside the existing 30-minute window.
            var current = (await store.GetMeshCentralDeploymentsAsync(5000, stoppingToken).ConfigureAwait(false))
                .FirstOrDefault(x => x.DeploymentId == deploymentId);
            if (current is not null && current.Status == "succeeded") return;

            await store.UpdateMeshCentralDeploymentAsync(
                deploymentId,
                "installing",
                "MeshCentral komut yanıtı 5 dakika içinde dönmedi; bu durum kurulum hatası sayılmadı. Exact Agent kaydı ve kimliği doğrulanmış heartbeat bekleniyor (azami toplam deployment penceresi: 30 dakika).",
                current?.CommandId,
                current?.AgentId ?? deployment.AgentId,
                stoppingToken).ConfigureAwait(false);

            try
            {
                await connectorService.SynchronizeAsync(connector, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDeploymentFailed(logger, deploymentId, ex);
            }
        }
        catch (Exception ex)
        {
            await store.UpdateMeshCentralDeploymentAsync(deploymentId, "failed", SafeDetail(ex.Message), null, deployment.AgentId, stoppingToken).ConfigureAwait(false);
        }
    }

    private static string SafeDetail(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown MeshCentral deployment failure.";
        var clean = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return clean.Length <= 900 ? clean : clean[..900];
    }
}
