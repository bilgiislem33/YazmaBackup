using System.Security.Cryptography;
using YazmaBackup.Domain;
using YazmaBackup.Infrastructure;

namespace YazmaBackup.Infrastructure.Tests;

public sealed class RepositoryIntegrityTests
{
    [Fact]
    public async Task PutChunk_rejects_content_that_does_not_match_declared_sha256()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var data = "critical-backup-data"u8.ToArray();
        var wrongHash = new string('0', 64);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.PutChunkAsync(wrongHash, data, CancellationToken.None));
    }

    [Fact]
    public async Task Encrypted_chunk_round_trips_exact_plaintext()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var data = RandomNumberGenerator.GetBytes(128 * 1024 + 17);
        var hash = Sha(data);

        await repository.PutChunkAsync(hash, data, CancellationToken.None);
        await using var stream = await repository.OpenChunkReadAsync(hash, CancellationToken.None);
        using var restored = new MemoryStream();
        await stream.CopyToAsync(restored);

        Assert.Equal(data, restored.ToArray());
        var storedPath = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.chunk", SearchOption.AllDirectories));
        var stored = await File.ReadAllBytesAsync(storedPath);
        Assert.True(RepositoryCryptoContext.IsEncrypted(stored));
        Assert.NotEqual(data, stored);
    }

    [Fact]
    public async Task Tampered_encrypted_chunk_fails_closed()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var data = "data-that-must-never-silently-corrupt"u8.ToArray();
        var hash = Sha(data);
        await repository.PutChunkAsync(hash, data, CancellationToken.None);

        var chunkPath = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.chunk", SearchOption.AllDirectories));
        var stored = await File.ReadAllBytesAsync(chunkPath);
        stored[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(chunkPath, stored);

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            repository.ContainsChunkAsync(hash, CancellationToken.None));
    }

    [Fact]
    public void Wrong_repository_key_cannot_open_existing_encrypted_repository()
    {
        using var directory = new TemporaryDirectory();
        using (var correct = Crypto("key-a"))
        {
            _ = new FileSystemBackupRepository(directory.Path, "repo-a", correct);
        }

        using var wrong = Crypto("key-b");
        Assert.ThrowsAny<CryptographicException>(() =>
            new FileSystemBackupRepository(directory.Path, "repo-a", wrong));
    }

    [Fact]
    public async Task Manifest_is_append_only_for_same_backup_id()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var first = Manifest("backup-1", "first.bin", "first"u8.ToArray());
        var conflicting = Manifest("backup-1", "changed.bin", "different"u8.ToArray());

        await repository.WriteManifestAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.WriteManifestAsync(conflicting, CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_manifest_is_rejected_before_restore_metadata_is_trusted()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var manifest = Manifest("backup-1", "critical.bin", "manifest-data"u8.ToArray());
        await repository.WriteManifestAsync(manifest, CancellationToken.None);

        var manifestPath = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.manifest", SearchOption.AllDirectories));
        var stored = await File.ReadAllBytesAsync(manifestPath);
        stored[^1] ^= 0x33;
        await File.WriteAllBytesAsync(manifestPath, stored);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadManifestAsync(manifest.AgentId, manifest.BackupId, CancellationToken.None));
    }

    [Fact]
    public void New_encrypted_repository_uses_authenticated_metadata_not_plaintext_metadata()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        _ = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);

        Assert.True(File.Exists(Path.Combine(directory.Path, "repository.meta")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "repository.json")));
    }

    [Fact]
    public async Task Retention_quarantine_keeps_a_recoverable_verified_manifest_copy()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var manifest = Manifest("backup-1", "critical.bin", "manifest-data"u8.ToArray());
        await repository.WriteManifestAsync(manifest, CancellationToken.None);

        await using var lease = await repository.AcquireMutationLeaseAsync(CancellationToken.None);
        await repository.QuarantineManifestAsync(manifest, DateTimeOffset.MinValue, CancellationToken.None);

        Assert.Empty(await repository.ListManifestsAsync(manifest.AgentId, null, CancellationToken.None));
        var quarantined = Assert.Single(await repository.ListQuarantinedManifestsAsync(CancellationToken.None));
        Assert.Equal(manifest.BackupId, quarantined.Manifest.BackupId);
        Assert.Equal(manifest.Files[0].Sha256, quarantined.Manifest.Files[0].Sha256);
        Assert.True(Directory.EnumerateFiles(directory.Path, "*.manifest", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Repository_enforces_manifest_immutability_before_quarantine()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var manifest = Manifest("backup-1", "critical.bin", "manifest-data"u8.ToArray());
        await repository.WriteManifestAsync(manifest, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.QuarantineManifestAsync(
            manifest, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None));

        Assert.Single(await repository.ListManifestsAsync(manifest.AgentId, null, CancellationToken.None));
        Assert.Empty(await repository.ListQuarantinedManifestsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Premature_quarantine_purge_is_rejected_by_repository()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var manifest = Manifest("backup-1", "critical.bin", "manifest-data"u8.ToArray());
        await repository.WriteManifestAsync(manifest, CancellationToken.None);
        await repository.QuarantineManifestAsync(manifest, DateTimeOffset.MinValue, CancellationToken.None);
        var quarantined = Assert.Single(await repository.ListQuarantinedManifestsAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PurgeQuarantinedManifestAsync(
            manifest.AgentId, manifest.BackupId, quarantined.QuarantinedAtUtc, CancellationToken.None));

        Assert.Single(await repository.ListQuarantinedManifestsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Repository_mutation_lease_serializes_independent_repository_instances()
    {
        using var directory = new TemporaryDirectory();
        using var firstCrypto = Crypto("key-a");
        using var secondCrypto = Crypto("key-a");
        var first = new FileSystemBackupRepository(directory.Path, "repo-a", firstCrypto);
        var second = new FileSystemBackupRepository(directory.Path, "repo-a", secondCrypto);

        await using var held = await first.AcquireMutationLeaseAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            second.AcquireMutationLeaseAsync(timeout.Token).AsTask());
    }

    [Fact]
    public async Task Quarantined_manifest_keeps_its_encryption_key_referenced()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var manifest = Manifest("backup-1", "critical.bin", "manifest-data"u8.ToArray());
        await repository.WriteManifestAsync(manifest, CancellationToken.None);
        await repository.QuarantineManifestAsync(manifest, DateTimeOffset.MinValue, CancellationToken.None);

        var referencedKeys = await repository.GetReferencedEncryptionKeyIdsAsync(CancellationToken.None);

        Assert.Contains("key-a", referencedKeys);
    }

    [Fact]
    public async Task Incomplete_quarantine_staging_directory_is_ignored()
    {
        using var directory = new TemporaryDirectory();
        using var crypto = Crypto("key-a");
        var repository = new FileSystemBackupRepository(directory.Path, "repo-a", crypto);
        var staging = Path.Combine(directory.Path, "quarantine", "manifests", "agent-test", "backup-1.staging-crash");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "quarantine.json"), "{}");

        Assert.Empty(await repository.ListQuarantinedManifestsAsync(CancellationToken.None));
    }

    private static RepositoryCryptoContext Crypto(string keyId)
    {
        var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("YazmaBackup.Tests/" + keyId));
        return new RepositoryCryptoContext(keyId, key);
    }

    private static BackupManifest Manifest(string backupId, string relativePath, byte[] bytes)
    {
        var hash = Sha(bytes);
        var file = new BackupFileEntry(
            relativePath,
            bytes.LongLength,
            DateTimeOffset.UtcNow,
            hash,
            [new BackupChunkRef(hash, bytes.Length)]);
        return new BackupManifest(
            "3",
            backupId,
            "agent-test",
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "yb-source")),
            DateTimeOffset.UtcNow,
            [file],
            bytes.LongLength,
            bytes.LongLength,
            1,
            0,
            "test",
            false,
            EncryptionKeyId: "key-a");
    }

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YazmaBackup.RepositoryTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
