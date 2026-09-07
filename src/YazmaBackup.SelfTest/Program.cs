using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using YazmaBackup.Application;
using YazmaBackup.ControlPlane;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using YazmaBackup.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), "YazmaBackupSelfTest", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var originalStateDirectory = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR");
Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", null);
try
{
    await EncryptedIncrementalBackupRestoreAsync(Path.Combine(root, "encrypted"));
    await RepositoryKeyRotationAsync(Path.Combine(root, "rotation"));
    await LegacyManifestMigrationAsync(Path.Combine(root, "legacy-repo"));
    await RetentionAsync(Path.Combine(root, "retention-repo"));
    await RansomwarePreflightAsync(Path.Combine(root, "ransomware"));
    await AppendOnlyAndImmutabilityAsync(Path.Combine(root, "immutability"));
    await StateSchemaMigrationAsync(Path.Combine(root, "state-migration"));
    RepositoryCircuitBreakerAsync(Path.Combine(root, "repository-circuit"));
    TransferTelemetryRegistryTest();
    await RepositoryWriteTelemetryAsync(Path.Combine(root, "write-telemetry"));
    await ControlPlaneLeaseAndSchedulerAsync(Path.Combine(root, "control-plane"));
    Console.WriteLine("PASS: YazmaBackup v1.2.0 self-test — AES-GCM, CDC dedup, incremental reuse, integrity restore, key rotation/scrub, retention, ransomware preflight blocking, append-only manifest and immutability window, legacy migration, enrollment, lease, scheduler with restore drill, schema 5→11 state migration and future-schema downgrade block, management auth/RBAC lockout, scoped API token, break-glass, OIDC identity binding, alarm lifecycle, cluster lease, repository-health scheduling, recovery-plan/runbook evidence, persistent repository circuit breaker, scoped MeshCentral status ingestion, signed recovery evidence, notification delivery idempotency, live transfer telemetry registry, granular restore, restore sandbox and audit verified.");
}
finally
{
    Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", originalStateDirectory);
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}


static async Task RepositoryWriteTelemetryAsync(string root)
{
    Directory.CreateDirectory(root);
    var observer = new CountingTransferObserver();
    var repository = new FileSystemBackupRepository(root, "telemetry-repository", transferObserver: observer);
    var bytes = DeterministicBytes(192 * 1024, 0x90A0B0C0);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    await repository.PutChunkAsync(hash, bytes, CancellationToken.None);
    Assert(observer.BytesWritten == bytes.Length, "repository transfer telemetry counts bytes only after successful repository writes");
}

static void TransferTelemetryRegistryTest()
{
    var registry = new TransferTelemetryRegistry();
    var agentId = Guid.NewGuid();
    var commandId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    registry.Record(agentId, "SELFTEST-PC", new AgentTransferTelemetryRequest(commandId, "backup", "active", "SELFTEST-REPO", 1024 * 1024, 512 * 1024, now.AddSeconds(-2), now));
    var active = registry.Snapshot();
    Assert(active.ActiveTransfers == 1, "live transfer telemetry registry exposes active transfer");
    Assert(active.TotalBytesPerSecond == 512 * 1024, "live transfer telemetry registry aggregates current bytes per second");
    registry.Record(agentId, "SELFTEST-PC", new AgentTransferTelemetryRequest(commandId, "backup", "completed", "SELFTEST-REPO", 2 * 1024 * 1024, 0, now.AddSeconds(-2), now.AddSeconds(1)));
    var completed = registry.Snapshot();
    Assert(completed.ActiveTransfers == 0, "completed transfer leaves active transfer set");
    Assert(completed.History.Count >= 1, "live transfer telemetry keeps bounded aggregate history");
}

static async Task EncryptedIncrementalBackupRestoreAsync(string root)
{
    var source = Path.Combine(root, "source");
    var repoRoot = Path.Combine(root, "repo");
    var restore = Path.Combine(root, "restore");
    var restoreTamper = Path.Combine(root, "restore-tamper");
    Directory.CreateDirectory(source);
    var data = DeterministicBytes(2 * 1024 * 1024, 0x20260823);
    await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), data);
    await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), data);

    var key = DeterministicBytes(32, 0xA55A1234);
    try
    {
        using var crypto = new RepositoryCryptoContext("key-1", key);
        var repo = new FileSystemBackupRepository(repoRoot, "repo-selftest", crypto);
        var tracker = new ScriptedChangeTracker(
            Change(true, Set(), "test:first"),
            Change(true, Set(), "test:no-changes"),
            Change(true, Set("a.bin"), "test:a-changed"));
        var engine = new BackupEngine(new ContentDefinedChunker(64 * 1024, 256 * 1024, 1024 * 1024), repo, new PassThroughSnapshotProvider(), tracker);

        var first = await engine.BackupDirectoryAsync("agent-test", source, CancellationToken.None);
        Assert(first.Files.Count == 2, "first encrypted backup file count");
        Assert(first.NewChunks > 0, "first encrypted backup created chunks");
        Assert(first.EncryptionKeyId == "key-1", "manifest records encryption key id");

        var firstChunk = first.Files[0].Chunks[0].Sha256;
        var chunkPath = Path.Combine(repoRoot, "chunks", firstChunk[..2], firstChunk[2..4], firstChunk + ".chunk");
        var rawChunk = await File.ReadAllBytesAsync(chunkPath);
        Assert(RepositoryCryptoContext.IsEncrypted(rawChunk), "chunk is encrypted at rest");
        var rawManifest = await File.ReadAllBytesAsync(repo.DescribeManifestLocation(first.AgentId, first.BackupId));
        Assert(RepositoryCryptoContext.IsEncrypted(rawManifest), "manifest is encrypted at rest");

        var second = await engine.BackupDirectoryAsync("agent-test", source, CancellationToken.None);
        Assert(second.UploadedBytes == 0, "unchanged backup uploaded zero bytes");
        Assert(second.ReusedFiles == 2, "USN-style incremental plan reuses unchanged files without rehash");

        var modified = data.ToArray();
        modified[12345] ^= 0x5A;
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), modified);
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.bin"), DateTime.UtcNow.AddSeconds(2));
        var third = await engine.BackupDirectoryAsync("agent-test", source, CancellationToken.None);
        Assert(third.ReusedFiles == 1, "only unchanged file reused after tracked change");
        Assert(third.ParentBackupId == second.BackupId, "incremental parent backup recorded");

        var restoreEngine = new RestoreEngine(repo);
        var summary = await restoreEngine.RestoreAsync("agent-test", third.BackupId, restore, overwriteExisting: false, CancellationToken.None);
        Assert(summary.RestoredFiles == 2, "encrypted restore file count");
        Assert((await File.ReadAllBytesAsync(Path.Combine(restore, "a.bin"))).SequenceEqual(modified), "encrypted restore content a");
        Assert((await File.ReadAllBytesAsync(Path.Combine(restore, "b.bin"))).SequenceEqual(data), "encrypted restore content b");

        var replay = await restoreEngine.RestoreAsync("agent-test", third.BackupId, restore, overwriteExisting: false, CancellationToken.None);
        Assert(replay.AlreadyPresentFiles == 2, "encrypted restore retry idempotent");

        var granularRoot = Path.Combine(root, "granular");
        var granular = await restoreEngine.RestoreSelectedAsync("agent-test", third.BackupId, granularRoot, ["a.bin"], overwriteExisting: false, CancellationToken.None);
        Assert(granular.RestoredFiles == 1 && File.Exists(Path.Combine(granularRoot, "a.bin")) && !File.Exists(Path.Combine(granularRoot, "b.bin")), "granular restore selects only requested paths");

        var sandboxRoot = Path.Combine(root, "sandbox");
        var sandbox = await restoreEngine.RestoreSandboxAsync("agent-test", third.BackupId, sandboxRoot, 10, 20L * 1024 * 1024, CancellationToken.None);
        Assert(sandbox.RestoredFiles == 2 && File.Exists(Path.Combine(sandboxRoot, "a.bin")), "restore sandbox verifies complete restore in isolated destination");

        var tamperHash = third.Files.SelectMany(f => f.Chunks).First().Sha256;
        var tamperPath = Path.Combine(repoRoot, "chunks", tamperHash[..2], tamperHash[2..4], tamperHash + ".chunk");
        var corrupt = await File.ReadAllBytesAsync(tamperPath);
        corrupt[^1] ^= 0x33;
        await File.WriteAllBytesAsync(tamperPath, corrupt);
        var integrityFailed = false;
        try
        {
            await restoreEngine.RestoreAsync("agent-test", third.BackupId, restoreTamper, overwriteExisting: false, CancellationToken.None);
        }
        catch (CryptographicException) { integrityFailed = true; }
        catch (InvalidDataException) { integrityFailed = true; }
        Assert(integrityFailed, "tampered encrypted chunk rejected");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}

static async Task RepositoryKeyRotationAsync(string root)
{
    var source = Path.Combine(root, "source");
    var repoRoot = Path.Combine(root, "repo");
    var restore = Path.Combine(root, "restore");
    Directory.CreateDirectory(source);
    await File.WriteAllBytesAsync(Path.Combine(source, "rotation.bin"), DeterministicBytes(512 * 1024, 0x99112233));
    var key1 = DeterministicBytes(32, 0x11111111);
    var key2 = DeterministicBytes(32, 0x22222222);
    string backupId;
    string chunkHash;
    try
    {
        using (var crypto1 = new RepositoryCryptoContext("key-old", key1))
        {
            var repo1 = new FileSystemBackupRepository(repoRoot, "repo-rotation", crypto1);
            var manifest = await new BackupEngine(new ContentDefinedChunker(64 * 1024, 256 * 1024, 1024 * 1024), repo1, new PassThroughSnapshotProvider())
                .BackupDirectoryAsync("agent-rotation", source, CancellationToken.None);
            backupId = manifest.BackupId;
            chunkHash = manifest.Files[0].Chunks[0].Sha256;
        }

        using (var rotating = new RepositoryCryptoContext("key-new", new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["key-old"] = key1,
            ["key-new"] = key2
        }))
        {
            var repo2 = new FileSystemBackupRepository(repoRoot, "repo-rotation", rotating);
            var beforeReferences = await repo2.GetReferencedEncryptionKeyIdsAsync(CancellationToken.None);
            Assert(beforeReferences.Contains("key-old"), "old key is referenced before rekey scrub");
            var scrub = await repo2.ScrubAsync(migrateLegacyPlaintext: true, CancellationToken.None);
            Assert(scrub.VerifiedChunks > 0, "rotation scrub verified chunks");
            Assert(scrub.MigratedChunks > 0, "rotation scrub re-encrypted old key chunks");
            var afterReferences = await repo2.GetReferencedEncryptionKeyIdsAsync(CancellationToken.None);
            Assert(!afterReferences.Contains("key-old") && afterReferences.Contains("key-new"), "old key has no repository references after rekey scrub");
        }

        var chunkPath = Path.Combine(repoRoot, "chunks", chunkHash[..2], chunkHash[2..4], chunkHash + ".chunk");
        var raw = await File.ReadAllBytesAsync(chunkPath);
        Assert(RepositoryCryptoContext.ReadKeyId(raw) == "key-new", "chunk re-encrypted with active key");

        using var crypto2Only = new RepositoryCryptoContext("key-new", key2);
        var repo3 = new FileSystemBackupRepository(repoRoot, "repo-rotation", crypto2Only);
        var summary = await new RestoreEngine(repo3).RestoreAsync("agent-rotation", backupId, restore, false, CancellationToken.None);
        Assert(summary.RestoredFiles == 1, "restore succeeds after old key removal simulation");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key1);
        CryptographicOperations.ZeroMemory(key2);
    }
}

static async Task LegacyManifestMigrationAsync(string repositoryRoot)
{
    const string agentId = "legacy-agent";
    const string backupId = "legacy-backup";
    var repository = new FileSystemBackupRepository(repositoryRoot, "legacy-repository");
    var content = DeterministicBytes(128 * 1024, 0x12345678);
    var chunkHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    await repository.PutChunkAsync(chunkHash, content, CancellationToken.None);
    var manifestDirectory = Path.Combine(repositoryRoot, "manifests", agentId);
    Directory.CreateDirectory(manifestDirectory);
    var manifestPath = Path.Combine(manifestDirectory, backupId + ".json");
    var createdAt = DateTimeOffset.UtcNow;
    var legacy = new
    {
        schemaVersion = "1",
        backupId,
        agentId,
        sourceRoot = Path.Combine(Path.GetTempPath(), "Legacy"),
        createdAtUtc = createdAt,
        files = new[]
        {
            new
            {
                relativePath = "legacy.bin",
                length = (long)content.Length,
                lastWriteTimeUtc = createdAt,
                chunks = new[] { new { sha256 = chunkHash, length = content.Length } }
            }
        },
        logicalBytes = (long)content.Length,
        uploadedBytes = (long)content.Length,
        newChunks = 1,
        reusedChunks = 0
    };
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    var upgraded = await repository.ReadManifestAsync(agentId, backupId, CancellationToken.None);
    Assert(upgraded.SchemaVersion == "3", "legacy manifest upgraded to schema 3");
    Assert(upgraded.Files.Count == 1 && upgraded.Files[0].Sha256 == chunkHash, "legacy full-file hash reconstructed");
    Assert(!File.Exists(manifestPath), "legacy manifest file removed after successful migration");
    Assert(File.Exists(repository.DescribeManifestLocation(agentId, backupId) + ".sha256"), "upgraded manifest integrity sidecar created");
}

static async Task RetentionAsync(string repositoryRoot)
{
    const string agentId = "retention-agent";
    var sourceRoot = Path.Combine(Path.GetTempPath(), "RetentionSource");
    var repository = new FileSystemBackupRepository(repositoryRoot, "retention-repository");
    var now = DateTimeOffset.UtcNow;
    for (var index = 0; index < 6; index++)
    {
        var manifest = new BackupManifest(
            "3", $"retention-{index}", agentId, sourceRoot, now.AddHours(-index), [], 0, 0, 0, 0,
            "test", false, null, "test", 0, null);
        await repository.WriteManifestAsync(manifest, CancellationToken.None);
    }

    var result = await new RetentionManager(repository).ApplyAsync(agentId, sourceRoot, new RetentionPolicy(KeepLast: 2, KeepDaily: 0, KeepWeekly: 0, KeepMonthly: 0, ImmutabilityHours: 0), CancellationToken.None);
    Assert(result.DeletedRestorePoints == 4, "retention deletes restore points outside keep-last");
    var remaining = await repository.ListManifestsAsync(agentId, sourceRoot, CancellationToken.None);
    Assert(remaining.Count == 2, "retention keeps configured restore point count");
}

static async Task RansomwarePreflightAsync(string root)
{
    var source = Path.Combine(root, "source");
    var repoRoot = Path.Combine(root, "repo");
    Directory.CreateDirectory(source);
    for (var i = 0; i < 120; i++)
        await File.WriteAllTextAsync(Path.Combine(source, $"document-{i:D3}.txt"), new string((char)('A' + (i % 20)), 8192));

    var repository = new FileSystemBackupRepository(repoRoot, "ransomware-selftest");
    var baseline = new BackupEngine(new ContentDefinedChunker(16 * 1024, 32 * 1024, 64 * 1024), repository, new PassThroughSnapshotProvider());
    var first = await baseline.BackupDirectoryAsync("agent-ransomware", source, CancellationToken.None);
    Assert(first.Files.Count == 120, "ransomware baseline created");

    for (var i = 0; i < 80; i++)
    {
        var original = Path.Combine(source, $"document-{i:D3}.txt");
        var encrypted = original + ".locked";
        File.Move(original, encrypted);
        await File.WriteAllBytesAsync(encrypted, RandomNumberGenerator.GetBytes(8192));
        File.SetLastWriteTimeUtc(encrypted, DateTime.UtcNow.AddSeconds(2));
    }

    var policy = new ProtectionPolicy(
        Enabled: true,
        MinimumChangedFiles: 20,
        ChangedFileRatioThreshold: 0.25,
        MinimumExtensionChanges: 10,
        ExtensionChangeRatioThreshold: 0.25,
        MinimumEntropySamples: 8,
        HighEntropyRatioThreshold: 0.25,
        EntropyThresholdBitsPerByte: 7.2,
        AutoLockOnDetection: true);
    var protectedEngine = new BackupEngine(
        new ContentDefinedChunker(16 * 1024, 32 * 1024, 64 * 1024),
        repository,
        new PassThroughSnapshotProvider(),
        changeTracker: null,
        protectionGuard: new RansomwareProtectionGuard());

    var blocked = false;
    try
    {
        await protectedEngine.BackupDirectoryAsync("agent-ransomware", source, policy, CancellationToken.None);
    }
    catch (RansomwareSuspectedException ex)
    {
        blocked = ex.Assessment.Suspicious
            && ex.Assessment.ExtensionChangeCount >= 10
            && ex.Assessment.HighEntropySampleCount >= 8;
    }
    Assert(blocked, "ransomware-like mass extension and entropy rewrite blocked before manifest commit");
    var manifests = await repository.ListManifestsAsync("agent-ransomware", source, CancellationToken.None);
    Assert(manifests.Count == 1, "blocked ransomware backup did not create a restore point");
}

static async Task AppendOnlyAndImmutabilityAsync(string root)
{
    var repository = new FileSystemBackupRepository(root, "immutability-selftest");
    var now = DateTimeOffset.UtcNow;
    var source = Path.Combine(Path.GetTempPath(), "ImmutabilitySource");
    var first = new BackupManifest("3", "fixed-backup-id", "immutability-agent", source, now, [], 0, 0, 0, 0, "test", false);
    await repository.WriteManifestAsync(first, CancellationToken.None);

    var overwriteRejected = false;
    try
    {
        await repository.WriteManifestAsync(first with { LogicalBytes = 1 }, CancellationToken.None);
    }
    catch (InvalidDataException) { overwriteRejected = true; }
    Assert(overwriteRejected, "append-only manifest rejects different content for same backup id");

    for (var i = 1; i <= 2; i++)
    {
        var manifest = new BackupManifest("3", $"immutable-{i}", "immutability-agent", source, now.AddMinutes(-i), [], 0, 0, 0, 0, "test", false);
        await repository.WriteManifestAsync(manifest, CancellationToken.None);
    }

    var retention = await new RetentionManager(repository).ApplyAsync(
        "immutability-agent", source,
        new RetentionPolicy(KeepLast: 1, KeepDaily: 0, KeepWeekly: 0, KeepMonthly: 0, ImmutabilityHours: 24),
        CancellationToken.None);
    Assert(retention.DeletedRestorePoints == 0, "immutability window prevents restore-point deletion");
    Assert(retention.KeptRestorePoints == 3, "immutability window keeps all recent restore points");
}

static async Task StateSchemaMigrationAsync(string stateRoot)
{
    var previousStateDirectory = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR");
    try
    {
        Directory.CreateDirectory(stateRoot);
        Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", stateRoot);

        var policyId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var oldState = new
        {
            schemaVersion = "5",
            agents = new Dictionary<Guid, object>(),
            commands = new Dictionary<Guid, object>(),
            enrollmentGrants = new Dictionary<Guid, object>(),
            backupPolicies = new Dictionary<Guid, object>
            {
                [policyId] = new
                {
                    policyId,
                    name = "Legacy Policy",
                    agentId,
                    sourcePath = Path.Combine(stateRoot, "source"),
                    repositoryRoot = Path.Combine(stateRoot, "repo"),
                    repositoryId = "legacy-repo",
                    requireSnapshot = true,
                    intervalMinutes = 60,
                    activeBytesPerSecond = 1024L * 1024,
                    idleBytesPerSecond = 0L,
                    userIdleThresholdSeconds = 300,
                    retention = new RetentionPolicy(),
                    enabled = true,
                    createdAtUtc = now,
                    lastScheduledAtUtc = (DateTimeOffset?)null,
                    nextRunAtUtc = now.AddHours(1),
                    protection = new ProtectionPolicy()
                }
            },
            managementUsers = new Dictionary<Guid, object>(),
            auditEvents = new Dictionary<Guid, object>()
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(stateRoot, "control-plane-state.json"), JsonSerializer.Serialize(oldState, jsonOptions));

        using (var store = new StateStore(new TestEnvironment(stateRoot)))
        {
            var policies = await store.GetBackupPoliciesAsync(CancellationToken.None);
            Assert(policies.Count == 1, "schema 5 migration loads exactly one legacy policy from isolated test state");
            var migrated = policies[0];
            Assert(migrated.RestoreDrillIntervalDays == 7, "schema 5 policy receives safe 7-day restore-drill default");
            Assert(migrated.NextRestoreDrillAtUtc > now, "schema 5 policy receives future restore-drill schedule");
            await store.SetBackupPolicyEnabledAsync(policyId, true, CancellationToken.None);
            var persisted = await File.ReadAllTextAsync(Path.Combine(stateRoot, "control-plane-state.json"));
            Assert(persisted.Contains("\"schemaVersion\": \"11\"", StringComparison.Ordinal), "normalized state persists as schema 11 on next atomic commit");
        }

        var futureRoot = stateRoot + "-future";
        Directory.CreateDirectory(futureRoot);
        Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", futureRoot);
        var futureState = JsonSerializer.Serialize(new
        {
            schemaVersion = "12",
            agents = new Dictionary<Guid, object>(),
            commands = new Dictionary<Guid, object>(),
            enrollmentGrants = new Dictionary<Guid, object>(),
            backupPolicies = new Dictionary<Guid, object>(),
            managementUsers = new Dictionary<Guid, object>(),
            auditEvents = new Dictionary<Guid, object>()
        }, jsonOptions);
        var futureStatePath = Path.Combine(futureRoot, "control-plane-state.json");
        await File.WriteAllTextAsync(futureStatePath, futureState);
        var schema11Backup = JsonSerializer.Serialize(new
        {
            schemaVersion = "11",
            agents = new Dictionary<Guid, object>(),
            commands = new Dictionary<Guid, object>(),
            enrollmentGrants = new Dictionary<Guid, object>(),
            backupPolicies = new Dictionary<Guid, object>(),
            managementUsers = new Dictionary<Guid, object>(),
            auditEvents = new Dictionary<Guid, object>()
        }, jsonOptions);
        await File.WriteAllTextAsync(futureStatePath + ".bak", schema11Backup);
        var downgradeBlocked = false;
        try { using var futureStore = new StateStore(new TestEnvironment(futureRoot)); }
        catch (Exception ex) when (ex.GetType().Name == "UnsupportedStateSchemaException") { downgradeBlocked = true; }
        Assert(downgradeBlocked, "future control-plane state schema blocks binary downgrade even when an older .bak exists");
    }
    finally
    {
        Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", previousStateDirectory);
    }
}

static void RepositoryCircuitBreakerAsync(string root)
{
    Directory.CreateDirectory(root);
    var stateFile = Path.Combine(root, "circuit.json");
    var breaker = new RepositoryCircuitBreaker(stateFile);
    var now = DateTimeOffset.UtcNow;
    breaker.ReportFailure("LAB-selftest", new IOException("simulated outage 1"), now);
    breaker.ReportFailure("LAB-selftest", new IOException("simulated outage 2"), now.AddSeconds(1));
    var opened = breaker.ReportFailure("LAB-selftest", new IOException("simulated outage 3"), now.AddSeconds(2));
    Assert(opened.OpenUntilUtc > now, "repository circuit opens after third transient failure");
    var persisted = new RepositoryCircuitBreaker(stateFile).Snapshot().Single(x => x.RepositoryId == "LAB-selftest");
    Assert(persisted.ConsecutiveFailures == 3 && persisted.OpenUntilUtc is not null, "repository circuit state persists across Agent restart");
    breaker.ReportSuccess("LAB-selftest");
    Assert(!breaker.Snapshot().Single(x => x.RepositoryId == "LAB-selftest").IsOpen(DateTimeOffset.UtcNow), "successful repository access closes circuit");
}

static async Task ControlPlaneLeaseAndSchedulerAsync(string stateRoot)
{
    Directory.CreateDirectory(stateRoot);
    var previousStateDirectory = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR");
    Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", null);
    try
    {
        using var rsa = RSA.Create();
        rsa.KeySize = 3072;
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        using var store = new StateStore(new TestEnvironment(stateRoot));
        var (grant, enrollmentToken) = await store.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(10), 1, CancellationToken.None);
        Assert(grant.RemainingUses == 1, "enrollment grant use count");

        var (agent, accessToken) = await store.EnrollAgentAsync(enrollmentToken, "SELFTEST-PC", "Windows", "1.2.0", ["test"], publicPem, CancellationToken.None);
        Assert(!string.IsNullOrWhiteSpace(accessToken), "agent access token issued");
        var replayEnrollmentRejected = false;
        try
        {
            await store.EnrollAgentAsync(enrollmentToken, "SELFTEST-PC2", "Windows", "1.2.0", ["test"], publicPem, CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
            replayEnrollmentRejected = true;
        }
        Assert(replayEnrollmentRejected, "one-use enrollment token replay rejected");

        var first = await store.EnqueueAsync(agent.AgentId, AgentCommandType.BrowsePath, "{}", "same-operation", CancellationToken.None);
        var duplicate = await store.EnqueueAsync(agent.AgentId, AgentCommandType.BrowsePath, "{}", "same-operation", CancellationToken.None);
        Assert(first.CommandId == duplicate.CommandId, "idempotency key deduplicates command enqueue");

        var claimed = await store.ClaimNextAsync(agent.AgentId, CancellationToken.None) ?? throw new InvalidOperationException("SELFTEST FAILED: command was not claimable");
        Assert(claimed.LeaseId is not null && claimed.LeaseExpiresAtUtc is not null, "command lease created");
        Assert(claimed.AttemptCount == 1, "first command delivery attempt");
        var renewed = await store.RenewLeaseAsync(agent.AgentId, claimed.CommandId, claimed.LeaseId!.Value, CancellationToken.None);
        Assert(renewed >= claimed.LeaseExpiresAtUtc!.Value, "command lease renewed");

        await store.CompleteAsync(agent.AgentId, claimed.CommandId, claimed.LeaseId.Value, true, "{}", null, CancellationToken.None);
        await store.CompleteAsync(agent.AgentId, claimed.CommandId, claimed.LeaseId.Value, true, "{}", null, CancellationToken.None);
        var completed = await store.GetCommandAsync(claimed.CommandId, CancellationToken.None);
        Assert(completed?.CompletedAtUtc is not null && completed.Succeeded, "command completion idempotent");

        var due = DateTimeOffset.UtcNow;
        var policy = new BackupPolicyRecord(
            Guid.NewGuid(), "SelfTest Policy", agent.AgentId, Path.Combine(stateRoot, "source"), Path.Combine(stateRoot, "repo"), "selftest-repo",
            true, 60, 1024 * 1024, 0, 300, new RetentionPolicy(2, 1, 1, 1), true, due, null, due,
            new ProtectionPolicy(), RestoreDrillIntervalDays: 1, LastRestoreDrillScheduledAtUtc: null, NextRestoreDrillAtUtc: due);
        await store.CreateBackupPolicyAsync(policy, CancellationToken.None);

        var pilotService = new PilotReadinessService(store, store);
        var templates = pilotService.GetTemplates();
        Assert(templates.Count >= 4 && templates.Any(x => x.TemplateId == "finance-critical"), "pilot center exposes curated policy templates");
        var bulkPolicies = new[]
        {
            policy with { PolicyId = Guid.NewGuid(), Name = "Bulk SelfTest A", CreatedAtUtc = due.AddSeconds(1), Enabled = false },
            policy with { PolicyId = Guid.NewGuid(), Name = "Bulk SelfTest B", CreatedAtUtc = due.AddSeconds(1), Enabled = false }
        };
        var bulkCreated = await store.CreateBackupPoliciesAsync(bulkPolicies, CancellationToken.None);
        Assert(bulkCreated.Count == 2, "bulk policy creation commits validated policies together");
        var countBeforeRejectedBulk = (await store.GetBackupPoliciesAsync(CancellationToken.None)).Count;
        var rejectedBulk = false;
        try
        {
            await store.CreateBackupPoliciesAsync(
            [
                policy with { PolicyId = Guid.NewGuid(), Name = "Should Not Commit", Enabled = false },
                policy with { PolicyId = Guid.NewGuid(), AgentId = Guid.NewGuid(), Name = "Invalid Agent", Enabled = false }
            ], CancellationToken.None);
        }
        catch (KeyNotFoundException) { rejectedBulk = true; }
        Assert(rejectedBulk && (await store.GetBackupPoliciesAsync(CancellationToken.None)).Count == countBeforeRejectedBulk, "bulk policy validation failure leaves state unchanged");
        var pilotSummary = await pilotService.BuildSummaryAsync(CancellationToken.None);
        Assert(pilotSummary.Checks.Count >= 7 && pilotSummary.Score is >= 0 and <= 100, "pilot readiness summary returns bounded evidence-based score");

        var scheduled = await store.EnqueueDueBackupPoliciesAsync(due.AddSeconds(1), CancellationToken.None);
        Assert(scheduled == 2, "due policy atomically enqueues backup and restore-drill commands");
        var duplicateSchedule = await store.EnqueueDueBackupPoliciesAsync(due.AddSeconds(1), CancellationToken.None);
        Assert(duplicateSchedule == 0, "scheduler does not enqueue duplicate backup or restore-drill command for same period");
        var policies = await store.GetBackupPoliciesAsync(CancellationToken.None);
        Assert(policies.Single(x => x.PolicyId == policy.PolicyId).NextRunAtUtc > due, "scheduler advances next backup run atomically");
        Assert(policies.Single(x => x.PolicyId == policy.PolicyId).NextRestoreDrillAtUtc > due, "scheduler advances next restore-drill run atomically");
        var scheduledCommands = await store.GetRecentCommandsAsync(20, CancellationToken.None);
        Assert(scheduledCommands.Any(c => c.Type == AgentCommandType.RestoreDrill), "scheduler creates restore-drill command");

        var recoveryPlan = await store.CreateRecoveryPlanAsync("SelfTest DR", [policy.PolicyId], 1, 60, 30, true, CancellationToken.None);
        var resilienceScheduled = await store.EnqueueDueResilienceAsync(due.AddSeconds(2), CancellationToken.None);
        Assert(resilienceScheduled >= 2, "resilience orchestrator schedules repository health and recovery drill commands");
        var recoveryRuns = await store.GetRecoveryRunsAsync(10, CancellationToken.None);
        Assert(recoveryRuns.Any(r => r.PlanId == recoveryPlan.PlanId && r.Targets.Count == 1), "recovery plan creates auditable run with target evidence");
        Assert((await store.GetRecentCommandsAsync(50, CancellationToken.None)).Any(c => c.Type == AgentCommandType.RepositoryHealthScan), "repository health command is scheduled");

        var meshLink = await store.UpsertMeshCentralLinkAsync(agent.AgentId, "https://mesh.selftest.example", "node/selftest/opaque", CancellationToken.None);
        Assert(meshLink.AgentId == agent.AgentId && meshLink.NodeId == "node/selftest/opaque", "MeshCentral node link preserves opaque node identity");

        var runbook = await store.CreateRecoveryRunbookAsync("SelfTest Enterprise DR", [recoveryPlan.PlanId], 240, 30, true, CancellationToken.None);
        var fabricTransitions = await store.AdvanceProductionFabricAsync(due.AddSeconds(3), CancellationToken.None);
        Assert(fabricTransitions >= 2, "production fabric starts due recovery runbook and first recovery-plan step");
        var runbookRun = (await store.GetRecoveryRunbookRunsAsync(10, CancellationToken.None)).Single(x => x.RunbookId == runbook.RunbookId);
        Assert(runbookRun.Steps.Count == 1 && runbookRun.Steps[0].RecoveryRunId is not null, "recovery runbook binds ordered step to recovery run evidence");

        var integration = await store.CreateIntegrationCredentialAsync("SelfTest Mesh", "meshcentral-status", TimeSpan.FromDays(30), CancellationToken.None);
        Assert(integration.PlaintextToken.StartsWith("ybit_", StringComparison.Ordinal) && !string.Equals(integration.Record.TokenHash, integration.PlaintextToken, StringComparison.Ordinal), "integration credential secret is returned once and only hash is stored");
        var integrationIdentity = await store.AuthenticateIntegrationCredentialAsync(integration.PlaintextToken, "meshcentral-status", CancellationToken.None);
        Assert(integrationIdentity is not null, "MeshCentral integration credential authenticates for its scoped purpose");
        var syncEventId = Guid.NewGuid();
        var receivedAt = DateTimeOffset.UtcNow;
        var sync = await store.ReportMeshCentralSyncAsync(integration.Record.CredentialId, syncEventId, agent.AgentId, meshLink.NodeId, "online", "installed", receivedAt, receivedAt, CancellationToken.None);
        var syncReplay = await store.ReportMeshCentralSyncAsync(integration.Record.CredentialId, syncEventId, agent.AgentId, meshLink.NodeId, "online", "installed", receivedAt, receivedAt.AddSeconds(1), CancellationToken.None);
        Assert(sync.EventId == syncReplay.EventId && (await store.GetMeshCentralSyncEventsAsync(10, CancellationToken.None)).Count(x => x.EventId == syncEventId) == 1, "MeshCentral status ingestion is idempotent by event id");
        Assert((await store.GetMeshCentralLinksAsync(CancellationToken.None)).Single(x => x.AgentId == agent.AgentId).LastKnownNodeStatus == "online", "MeshCentral status ingestion updates linked node health");

        var fleetConnector = new MeshCentralConnectorRecord(Guid.NewGuid(), "SelfTest Fleet", "https://mesh.selftest.example", "svc-yazmabackup", "password", "protected-selftest", null, true, 5, receivedAt, receivedAt, null, null, null, null, null);
        await store.SaveMeshCentralConnectorAsync(fleetConnector, CancellationToken.None);
        Assert((await store.GetMeshCentralConnectorAsync(CancellationToken.None))?.ConnectorId == fleetConnector.ConnectorId, "MeshCentral fleet connector persistence survives store roundtrip");
        var fleetDevice = new MeshCentralInventoryDeviceRecord("node/selftest/fleet", fleetConnector.ConnectorId, agent.MachineName, agent.MachineName, null, "SelfTest", true, receivedAt, agent.AgentId, "matched", "hostname:SELFTEST-PC", agent.AgentVersion);
        await store.SaveMeshCentralInventoryAsync(fleetConnector.ConnectorId, [fleetDevice], receivedAt, "selftest", CancellationToken.None);
        var fleetSummary = await store.GetMeshCentralFleetSummaryAsync(CancellationToken.None);
        Assert(fleetSummary.TotalDevices == 1 && fleetSummary.MatchedAgents == 1, "MeshCentral fleet inventory persists automatic agent match evidence");
        var deployment = new MeshCentralDeploymentRecord(Guid.NewGuid(), fleetConnector.ConnectorId, fleetDevice.NodeId, fleetDevice.Name, null, "dispatched", receivedAt, receivedAt, "selftest", "cmd-selftest");
        await store.AddMeshCentralDeploymentAsync(deployment, CancellationToken.None);
        await store.UpdateMeshCentralDeploymentAsync(deployment.DeploymentId, "succeeded", "heartbeat", "cmd-selftest", agent.AgentId, CancellationToken.None);
        Assert((await store.GetMeshCentralDeploymentsAsync(10, CancellationToken.None)).Single(x => x.DeploymentId == deployment.DeploymentId).Status == "succeeded", "MeshCentral deployment history preserves terminal evidence");

        var dataProtectionRoot = Path.Combine(stateRoot, "selftest-dp");
        Directory.CreateDirectory(dataProtectionRoot);
        var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(dataProtectionRoot));
        var evidenceService = new RecoveryEvidenceService(store, store, new EvidenceSigningKeyStore(new TestEnvironment(stateRoot), dataProtection));
        var evidence = await evidenceService.BuildAsync(runbookRun.RunId, CancellationToken.None) ?? throw new InvalidOperationException("SELFTEST FAILED: recovery evidence missing");
        Assert(string.Equals(evidence.PayloadSha256, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(evidence.PayloadJson))), StringComparison.OrdinalIgnoreCase), "recovery evidence payload hash is reproducible");
        using (var evidenceKey = ECDsa.Create())
        {
            evidenceKey.ImportFromPem(evidence.PublicKeyPem);
            Assert(evidenceKey.VerifyHash(Convert.FromHexString(evidence.PayloadSha256), Convert.FromBase64String(evidence.SignatureBase64)), "recovery evidence ECDSA signature verifies");
        }

        var bootstrapPassword = CreateTestPassword();
        var rotatedPassword = CreateTestPassword();
        var viewerPassword = CreateTestPassword();
        var wrongViewerPassword = CreateTestPassword();
        await store.EnsureBootstrapAdministratorAsync("admin", "SelfTest Administrator", bootstrapPassword, CancellationToken.None);
        var adminUser = await store.AuthenticateManagementUserAsync("admin", bootstrapPassword, CancellationToken.None);
        Assert(adminUser is not null && adminUser.MustChangePassword, "bootstrap administrator created with mandatory password change");
        var changedPassword = await store.ChangeManagementPasswordAsync(adminUser!.UserId, bootstrapPassword, rotatedPassword, CancellationToken.None);
        Assert(changedPassword, "administrator can rotate own bootstrap password");
        var rotatedAdmin = await store.AuthenticateManagementUserAsync("admin", rotatedPassword, CancellationToken.None);
        Assert(rotatedAdmin is not null && !rotatedAdmin.MustChangePassword, "rotated administrator authenticates without bootstrap flag");

        var viewer = await store.CreateManagementUserAsync("viewer1", "SelfTest Viewer", viewerPassword, [ManagementRoles.Viewer], true, CancellationToken.None);
        Assert(viewer.Roles.SequenceEqual([ManagementRoles.Viewer]), "management role persisted");
        for (var attempt = 0; attempt < 5; attempt++)
            Assert(await store.AuthenticateManagementUserAsync("viewer1", wrongViewerPassword, CancellationToken.None) is null, "wrong management password rejected");
        var lockedViewer = (await store.GetManagementUsersAsync(CancellationToken.None)).Single(u => u.UserId == viewer.UserId);
        Assert(lockedViewer.LockedUntilUtc > DateTimeOffset.UtcNow, "management account lockout activated after repeated failures");
        Assert(await store.AuthenticateManagementUserAsync("viewer1", viewerPassword, CancellationToken.None) is null, "locked management account remains blocked");

        var lastAdminDisableRejected = false;
        try { await store.SetManagementUserEnabledAsync(rotatedAdmin!.UserId, false, CancellationToken.None); }
        catch (InvalidOperationException) { lastAdminDisableRejected = true; }
        Assert(lastAdminDisableRejected, "last enabled administrator cannot be disabled");

        var audit = new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "selftest", "test-action", "POST", "/selftest", 200, "127.0.0.1", "selftest-correlation");
        await store.AppendAuditEventAsync(audit, CancellationToken.None);
        var auditRows = await store.GetAuditEventsAsync(10, CancellationToken.None);
        Assert(auditRows.Any(a => a.EventId == audit.EventId), "management audit trail persisted");

        var issuedToken = await store.CreateManagementApiTokenAsync("selftest-cli", [ManagementRoles.Operator], TimeSpan.FromHours(1), CancellationToken.None);
        Assert(issuedToken.PlaintextToken.StartsWith("ybmt_", StringComparison.Ordinal), "management API token uses dedicated prefix");
        Assert(!string.Equals(issuedToken.Record.TokenHash, issuedToken.PlaintextToken, StringComparison.Ordinal), "management API token is stored as a hash");
        var tokenIdentity = await store.AuthenticateManagementApiTokenAsync(issuedToken.PlaintextToken, CancellationToken.None);
        Assert(tokenIdentity is not null && tokenIdentity.Roles.SequenceEqual([ManagementRoles.Operator]), "management API token authenticates with scoped role");
        Assert(await store.RevokeManagementApiTokenAsync(issuedToken.Record.TokenId, CancellationToken.None), "management API token can be revoked");
        Assert(await store.AuthenticateManagementApiTokenAsync(issuedToken.PlaintextToken, CancellationToken.None) is null, "revoked management API token is rejected");

        var recoveryCodes = await store.CreateBreakGlassRecoveryCodesAsync(rotatedAdmin!.UserId, 2, TimeSpan.FromDays(1), CancellationToken.None);
        Assert(recoveryCodes.Count == 2 && recoveryCodes.All(c => c.StartsWith("YBRC-", StringComparison.Ordinal)), "break-glass recovery codes issued");
        var recoveredAdmin = await store.RedeemBreakGlassRecoveryCodeAsync("admin", recoveryCodes[0], CancellationToken.None);
        Assert(recoveredAdmin is not null && recoveredAdmin.MustChangePassword, "single-use break-glass code authenticates administrator and forces password change");
        Assert(await store.RedeemBreakGlassRecoveryCodeAsync("admin", recoveryCodes[0], CancellationToken.None) is null, "break-glass recovery code replay is rejected");
        var breakGlassResetPassword = CreateTestPassword();
        Assert(await store.ResetManagementPasswordAfterBreakGlassAsync(rotatedAdmin.UserId, breakGlassResetPassword, CancellationToken.None), "break-glass session can set a new password without the lost previous password");
        var postRecoveryAdmin = await store.AuthenticateManagementUserAsync("admin", breakGlassResetPassword, CancellationToken.None);
        Assert(postRecoveryAdmin is not null && !postRecoveryAdmin.MustChangePassword, "break-glass password reset clears forced-change state");

        var external = await store.FindOrProvisionExternalUserAsync("https://issuer.selftest", "subject-1", "external.user", "External User", [ManagementRoles.SecurityAdministrator], CancellationToken.None);
        var externalAgain = await store.FindOrProvisionExternalUserAsync("https://issuer.selftest", "subject-1", "different.claim", "External User Updated", [ManagementRoles.Viewer], CancellationToken.None);
        Assert(externalAgain.UserId == external.UserId && externalAgain.Roles.SequenceEqual([ManagementRoles.Viewer]), "OIDC identity remains issuer/subject-bound and role mapping refreshes");
        var externalAdminRejected = false;
        try { await store.FindOrProvisionExternalUserAsync("https://issuer.selftest", "subject-admin", "external.admin", "External Admin", [ManagementRoles.Administrator], CancellationToken.None); }
        catch (InvalidOperationException) { externalAdminRejected = true; }
        Assert(externalAdminRejected, "OIDC auto-provision cannot grant local administrator role");

        var alarmNow = DateTimeOffset.UtcNow;
        var alarm = await store.UpsertAlarmAsync("selftest:alarm", AlarmSeverity.Critical, "selftest", "SelfTest alarm", "details", agent.AgentId, alarmNow, CancellationToken.None);
        Assert(alarm.Status == AlarmStatus.Open, "alarm center opens fingerprinted alarm");
        var acknowledged = await store.AcknowledgeAlarmAsync(alarm.AlarmId, "selftest-admin", alarmNow.AddSeconds(1), CancellationToken.None);
        Assert(acknowledged?.Status == AlarmStatus.Acknowledged, "alarm can be acknowledged");
        var workflow = await store.UpdateAlarmWorkflowAsync(alarm.AlarmId, "backup-team", "investigating", alarmNow.AddMinutes(20), "selftest-admin", alarmNow.AddSeconds(1), CancellationToken.None);
        Assert(workflow?.AssignedTo == "backup-team" && workflow.OperatorNote == "investigating" && workflow.DueAtUtc == alarmNow.AddMinutes(20), "alarm workflow persists owner note and SLA due time");
        Assert(await store.ResolveAlarmAsync("selftest:alarm", alarmNow.AddSeconds(2), CancellationToken.None), "alarm can be resolved by fingerprint");
        Assert((await store.GetAlarmsAsync(10, true, CancellationToken.None)).Any(a => a.AlarmId == alarm.AlarmId && a.Status == AlarmStatus.Resolved), "resolved alarm remains auditable");

        var notificationAlarm = await store.UpsertAlarmAsync("selftest:notification", AlarmSeverity.Critical, "selftest", "Notification alarm", "delivery", agent.AgentId, alarmNow.AddSeconds(3), CancellationToken.None);
        var route = new NotificationRouteRecord(Guid.NewGuid(), "SelfTest Route", "https://alerts.selftest.example/yazmabackup", "protected-selftest-secret", [AlarmSeverity.Critical], true, alarmNow);
        await store.CreateNotificationRouteAsync(route, CancellationToken.None);
        var prepared = await store.PrepareNotificationDeliveriesAsync(alarmNow.AddSeconds(4), CancellationToken.None);
        Assert(prepared.Any(d => d.RouteId == route.RouteId && d.AlarmId == notificationAlarm.AlarmId), "notification router deduplicates alarm-version delivery intent");
        var preparedAgain = await store.PrepareNotificationDeliveriesAsync(alarmNow.AddSeconds(5), CancellationToken.None);
        Assert(preparedAgain.Count(d => d.RouteId == route.RouteId && d.AlarmId == notificationAlarm.AlarmId) == 1, "notification delivery intent remains idempotent for same alarm version");

        var leaseStart = DateTimeOffset.UtcNow;
        var leaderA = await store.TryAcquireOrRenewClusterLeaseAsync("selftest-leader", "node-a", TimeSpan.FromSeconds(30), leaseStart, CancellationToken.None);
        Assert(leaderA is not null && leaderA.Epoch == 1, "first cluster leadership lease acquires epoch 1");
        Assert(await store.TryAcquireOrRenewClusterLeaseAsync("selftest-leader", "node-b", TimeSpan.FromSeconds(30), leaseStart.AddSeconds(1), CancellationToken.None) is null, "competing node cannot steal an unexpired lease");
        var leaderB = await store.TryAcquireOrRenewClusterLeaseAsync("selftest-leader", "node-b", TimeSpan.FromSeconds(30), leaseStart.AddSeconds(31), CancellationToken.None);
        Assert(leaderB is not null && leaderB.Epoch == 2, "expired cluster lease transfers ownership with monotonic epoch");
    }
    finally
    {
        Environment.SetEnvironmentVariable("YAZMABACKUP_STATE_DIR", previousStateDirectory);
    }
}

static IncrementalChangeSet Change(bool canReuse, IReadOnlySet<string> changed, string mode) =>
    new(canReuse, changed, mode, _ => Task.CompletedTask);

static IReadOnlySet<string> Set(params string[] items) =>
    new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);

static string CreateTestPassword() =>
    "T-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

static byte[] DeterministicBytes(int length, uint seed)
{
    var data = new byte[length];
    var state = seed;
    for (var index = 0; index < data.Length; index++)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        data[index] = (byte)state;
    }
    return data;
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("SELFTEST FAILED: " + name);
}


sealed class CountingTransferObserver : ITransferObserver
{
    public long BytesWritten { get; private set; }
    public void OnBytesWritten(int bytes) => BytesWritten += bytes;
}

sealed class ScriptedChangeTracker : IIncrementalChangeTracker
{
    private readonly Queue<IncrementalChangeSet> _steps;

    public ScriptedChangeTracker(params IncrementalChangeSet[] steps)
    {
        _steps = new Queue<IncrementalChangeSet>(steps);
    }

    public Task<IncrementalChangeSet> CaptureAsync(string sourceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = sourceRoot;
        if (_steps.Count == 0) throw new InvalidOperationException("SELFTEST FAILED: scripted change tracker exhausted");
        return Task.FromResult(_steps.Dequeue());
    }
}

sealed class TestEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "YazmaBackup.SelfTest";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
