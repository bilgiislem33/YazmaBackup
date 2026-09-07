using System.Security.Cryptography;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class PolicySchedulerService(
    IControlPlaneStore store,
    RepositoryKeyVault keyVault,
    ILogger<PolicySchedulerService> logger) : BackgroundService
{
    private readonly string _nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var lease = await store.TryAcquireOrRenewClusterLeaseAsync("policy-scheduler", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                {
                    await EnsureDuePolicyRepositoryKeysAsync(now, stoppingToken).ConfigureAwait(false);
                    var count = await store.EnqueueDueBackupPoliciesAsync(now, stoppingToken).ConfigureAwait(false);
                    if (count > 0) LogPoliciesScheduled(logger, count, _nodeId, lease.Epoch);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task EnsureDuePolicyRepositoryKeysAsync(DateTimeOffset now, CancellationToken ct)
    {
        var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var agentMap = agents.ToDictionary(x => x.AgentId);
        var dueGroups = policies
            .Where(p => p.Enabled && p.NextRunAtUtc <= now)
            .GroupBy(p => (p.AgentId, RepositoryId: p.RepositoryId.Trim()), p => p)
            .ToArray();

        foreach (var group in dueGroups)
        {
            if (!agentMap.TryGetValue(group.Key.AgentId, out var agent) || !ValidAgentPublicKey(agent.KeyExchangePublicKeyPem))
                continue;

            var firstPolicy = group.OrderBy(p => p.NextRunAtUtc).First();
            var vaultKey = keyVault.GetOrCreate(group.Key.RepositoryId);
            byte[]? wrapped = null;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                wrapped = rsa.Encrypt(vaultKey.KeyMaterial, RSAEncryptionPadding.OaepSHA256);

                var idempotencyKey = $"policy:{firstPolicy.PolicyId:N}:repository-key:{firstPolicy.NextRunAtUtc.UtcDateTime.Ticks}";
                await store.EnqueueAsync(
                    agent.AgentId,
                    AgentCommandType.ProvisionRepositoryKey,
                    JsonSerializer.Serialize(new WrappedRepositoryKeyPayload(
                        group.Key.RepositoryId,
                        vaultKey.KeyId,
                        Convert.ToBase64String(wrapped),
                        true)),
                    idempotencyKey,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
                if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
            }
        }
    }

    private static bool ValidAgentPublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 8192) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 3072;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled {Count} YazmaBackup policy command(s) as leader node {NodeId}, epoch {Epoch}.")]
    private static partial void LogPoliciesScheduled(ILogger logger, int count, string nodeId, long epoch);

    [LoggerMessage(Level = LogLevel.Error, Message = "YazmaBackup policy scheduler iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);
}
