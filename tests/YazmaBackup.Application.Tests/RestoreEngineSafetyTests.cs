using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using YazmaBackup.Application;
using YazmaBackup.Domain;

namespace YazmaBackup.Application.Tests;

public sealed class RestoreEngineSafetyTests
{
    [Fact]
    public async Task Valid_chunk_restores_exact_original_bytes()
    {
        var bytes = "restore-integrity"u8.ToArray();
        var repository = RepositoryWithSingleFile("safe/data.bin", bytes);
        using var destination = new TemporaryDirectory();

        var summary = await new RestoreEngine(repository)
            .RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None);

        Assert.Equal(1, summary.RestoredFiles);
        Assert.Equal(bytes.Length, summary.RestoredBytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination.Path, "safe", "data.bin")));
    }

    [Fact]
    public async Task Corrupted_chunk_is_rejected_and_destination_is_not_published()
    {
        var original = "original-data"u8.ToArray();
        var repository = RepositoryWithSingleFile("critical.bin", original);
        var hash = Sha(original);
        repository.Chunks[hash] = "tampered-data"u8.ToArray();
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destination.Path, "critical.bin")));
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Truncated_chunk_is_rejected_and_temp_file_is_removed()
    {
        var original = "0123456789abcdef"u8.ToArray();
        var repository = RepositoryWithSingleFile("critical.bin", original);
        repository.Chunks[Sha(original)] = original[..5];
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destination.Path, "critical.bin")));
        Assert.Empty(Directory.EnumerateFiles(destination.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Chunk_with_trailing_bytes_is_rejected()
    {
        var original = "exact"u8.ToArray();
        var repository = RepositoryWithSingleFile("critical.bin", original);
        repository.Chunks[Sha(original)] = [.. original, 0x42];
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destination.Path, "critical.bin")));
    }

    [Fact]
    public async Task Missing_chunk_fails_closed()
    {
        var original = "missing-chunk"u8.ToArray();
        var repository = RepositoryWithSingleFile("critical.bin", original);
        repository.Chunks.Clear();
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(destination.Path, "critical.bin")));
    }

    [Fact]
    public async Task Manifest_path_cannot_escape_restore_root()
    {
        var bytes = "escape-attempt"u8.ToArray();
        var repository = RepositoryWithSingleFile(Path.Combine("..", "escaped.bin"), bytes);
        using var destination = new TemporaryDirectory();
        var escaped = Path.GetFullPath(Path.Combine(destination.Path, "..", "escaped.bin"));
        if (File.Exists(escaped)) File.Delete(escaped);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public async Task Existing_different_file_is_never_overwritten_without_permission()
    {
        var expected = "backup-version"u8.ToArray();
        var repository = RepositoryWithSingleFile("document.bin", expected);
        using var destination = new TemporaryDirectory();
        var target = Path.Combine(destination.Path, "document.bin");
        var existing = "local-version"u8.ToArray();
        await File.WriteAllBytesAsync(target, existing);

        await Assert.ThrowsAsync<IOException>(() =>
            new RestoreEngine(repository).RestoreAsync("agent-1", "backup-1", destination.Path, false, CancellationToken.None));

        Assert.Equal(existing, await File.ReadAllBytesAsync(target));
    }

    private static MemoryRepository RepositoryWithSingleFile(string relativePath, byte[] bytes)
    {
        var hash = Sha(bytes);
        var file = new BackupFileEntry(
            relativePath,
            bytes.LongLength,
            DateTimeOffset.UtcNow,
            hash,
            [new BackupChunkRef(hash, bytes.Length)]);
        var manifest = new BackupManifest(
            "3", "backup-1", "agent-1", "C:\\source", DateTimeOffset.UtcNow,
            [file], bytes.LongLength, bytes.LongLength, 1, 0, "test", false);
        var repository = new MemoryRepository(manifest);
        repository.Chunks[hash] = bytes.ToArray();
        return repository;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class MemoryRepository(BackupManifest manifest) : IBackupRepository
    {
        public Dictionary<string, byte[]> Chunks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? EncryptionKeyId => "test-key";

        public Task<BackupManifest> ReadManifestAsync(string agentId, string backupId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(manifest);
        }

        public Task<Stream> OpenChunkReadAsync(string sha256, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Chunks.TryGetValue(sha256, out var data))
                throw new FileNotFoundException("Chunk not found.", sha256);
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }

        public Task<bool> ContainsChunkAsync(string sha256, CancellationToken cancellationToken) => Task.FromResult(Chunks.ContainsKey(sha256));
        public Task PutChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { Chunks[sha256] = data.ToArray(); return Task.CompletedTask; }
        public Task WriteManifestAsync(BackupManifest value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<BackupManifest>> ListManifestsAsync(string agentId, string? sourceRoot, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BackupManifest>>([manifest]);
        public Task<IReadOnlyList<BackupManifest>> ListAllManifestsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BackupManifest>>([manifest]);
        public Task DeleteManifestAsync(string agentId, string backupId, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<string> EnumerateChunkHashesAsync([EnumeratorCancellation] CancellationToken cancellationToken) { foreach (var hash in Chunks.Keys) { cancellationToken.ThrowIfCancellationRequested(); yield return hash; await Task.Yield(); } }
        public Task<bool> DeleteChunkIfOlderThanAsync(string sha256, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(false);
        public string DescribeManifestLocation(string agentId, string backupId) => $"memory://{agentId}/{backupId}";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YazmaBackup.RestoreTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
