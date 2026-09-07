using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane;

public sealed class EvidenceSigningKeyStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    public EvidenceSigningKeyStore(IHostEnvironment environment, IDataProtectionProvider dataProtection)
    {
        var root = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR") ?? Path.Combine(environment.ContentRootPath, ".local");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "recovery-evidence-signing-key.protected");
        _protector = dataProtection.CreateProtector("YazmaBackup.RecoveryEvidenceSigningKey.v1");
    }

    public ECDsa OpenKey()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                var protectedValue = File.ReadAllText(_path, Encoding.UTF8);
                var privateBytes = Convert.FromBase64String(_protector.Unprotect(protectedValue));
                try
                {
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(privateBytes, out _);
                    return key;
                }
                finally { CryptographicOperations.ZeroMemory(privateBytes); }
            }

            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var material = created.ExportPkcs8PrivateKey();
            try
            {
                var protectedValue = _protector.Protect(Convert.ToBase64String(material));
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(protectedValue);
                        writer.Flush();
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temp, _path, overwrite: false);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { CryptographicOperations.ZeroMemory(material); }

            var clone = ECDsa.Create();
            clone.ImportParameters(created.ExportParameters(true));
            return clone;
        }
    }
}

public sealed class RecoveryEvidenceService(
    IProductionFabricStore fabricStore,
    IResilienceStore resilienceStore,
    EvidenceSigningKeyStore signingKeys)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<RecoveryEvidenceEnvelopeDto?> BuildAsync(Guid runbookRunId, CancellationToken ct)
    {
        var runbookRuns = await fabricStore.GetRecoveryRunbookRunsAsync(1000, ct).ConfigureAwait(false);
        var runbookRun = runbookRuns.FirstOrDefault(x => x.RunId == runbookRunId);
        if (runbookRun is null) return null;
        var runbooks = await fabricStore.GetRecoveryRunbooksAsync(ct).ConfigureAwait(false);
        var runbook = runbooks.FirstOrDefault(x => x.RunbookId == runbookRun.RunbookId);
        var recoveryRuns = await resilienceStore.GetRecoveryRunsAsync(1000, ct).ConfigureAwait(false);
        var referencedRunIds = runbookRun.Steps.Where(x => x.RecoveryRunId is not null).Select(x => x.RecoveryRunId!.Value).ToHashSet();
        var referencedRuns = recoveryRuns.Where(x => referencedRunIds.Contains(x.RunId)).OrderBy(x => x.StartedAtUtc).ToArray();
        var health = await resilienceStore.GetRepositoryHealthAsync(5000, ct).ConfigureAwait(false);
        var links = await resilienceStore.GetMeshCentralLinksAsync(ct).ConfigureAwait(false);
        var syncEvents = await fabricStore.GetMeshCentralSyncEventsAsync(5000, ct).ConfigureAwait(false);
        var agentIds = referencedRuns.SelectMany(x => x.Targets).Select(x => x.AgentId).Where(x => x != Guid.Empty).Distinct().ToHashSet();
        var latestHealth = health.Where(x => agentIds.Contains(x.AgentId)).GroupBy(x => x.AgentId).Select(g => g.OrderByDescending(x => x.MeasuredAtUtc).First()).OrderBy(x => x.AgentId).ToArray();
        var linked = links.Where(x => agentIds.Contains(x.AgentId)).OrderBy(x => x.AgentId).ToArray();
        var latestSync = syncEvents.Where(x => agentIds.Contains(x.AgentId)).GroupBy(x => x.AgentId).Select(g => g.OrderByDescending(x => x.ReceivedAtUtc).First()).OrderBy(x => x.AgentId).ToArray();
        var generatedAt = DateTimeOffset.UtcNow;

        var payload = new
        {
            schema = "YazmaBackup.RecoveryEvidence.v1",
            productVersion = "1.2.0",
            generatedAtUtc = generatedAt,
            runbook,
            runbookRun,
            recoveryRuns = referencedRuns,
            repositoryHealth = latestHealth,
            meshCentralLinks = linked,
            meshCentralSync = latestSync
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var hash = SHA256.HashData(payloadBytes);
        using var key = signingKeys.OpenKey();
        var signature = key.SignHash(hash);
        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        return new RecoveryEvidenceEnvelopeDto(
            "recovery-runbook",
            "1.2.0",
            generatedAt,
            payloadJson,
            Convert.ToHexString(hash).ToLowerInvariant(),
            "ECDSA_P256_SHA256",
            Convert.ToBase64String(signature),
            publicKeyPem);
    }
}
