using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using YazmaBackup.Application;
using YazmaBackup.Domain;

namespace YazmaBackup.Application.Tests;

public sealed class BackupEngineSafetyTests
{
    [Fact]
    public async Task Missing_source_fails_before_repository_commit()
    {
        var repository = new RecordingRepository();
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider());
        var missing = Path.Combine(Path.GetTempPath(), "yb-missing-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            engine.BackupDirectoryAsync("agent-1", missing, CancellationToken.None));

        Assert.Empty(repository.WrittenManifests);
        Assert.Empty(repository.StoredChunks);
    }

    [Fact]
    public async Task Manifest_write_failure_does_not_commit_incremental_checkpoint()
    {
        using var source = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "critical.txt"), "data that must survive");

        var checkpointCommits = 0;
        var tracker = new RecordingChangeTracker(() => checkpointCommits++);
        var repository = new RecordingRepository { FailManifestWrite = true };
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider(), tracker);

        await Assert.ThrowsAsync<IOException>(() =>
            engine.BackupDirectoryAsync("agent-1", source.Path, CancellationToken.None));

        Assert.Equal(0, checkpointCommits);
        Assert.Empty(repository.WrittenManifests);
    }

    [Fact]
    public async Task Successful_backup_commits_checkpoint_only_after_manifest_is_durable()
    {
        using var source = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "critical.txt"), "durable-data");

        var repository = new RecordingRepository();
        var commitObservedDurableManifest = false;
        var tracker = new RecordingChangeTracker(() =>
        {
            commitObservedDurableManifest = repository.WrittenManifests.Count == 1;
        });
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider(), tracker);

        var manifest = await engine.BackupDirectoryAsync("agent-1", source.Path, CancellationToken.None);

        Assert.True(commitObservedDurableManifest);
        Assert.Single(repository.WrittenManifests);
        Assert.Equal(manifest.BackupId, repository.WrittenManifests[0].BackupId);
    }

    [Fact]
    public async Task Manifest_file_and_chunk_hashes_match_source_bytes()
    {
        using var source = new TemporaryDirectory();
        var bytes = "YazmaBackup integrity invariant"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source.Path, "integrity.bin"), bytes);

        var repository = new RecordingRepository();
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider());

        var manifest = await engine.BackupDirectoryAsync("agent-1", source.Path, CancellationToken.None);

        var file = Assert.Single(manifest.Files);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expectedHash, file.Sha256);
        var chunk = Assert.Single(file.Chunks);
        Assert.Equal(expectedHash, chunk.Sha256);
        Assert.Equal(bytes.Length, chunk.Length);
        Assert.True(repository.StoredChunks.TryGetValue(expectedHash, out var stored));
        Assert.Equal(bytes, stored);
    }

    [Fact]
    public async Task Pre_cancelled_backup_never_writes_manifest_or_checkpoint()
    {
        using var source = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "cancel.txt"), "cancel-me");

        var commits = 0;
        var repository = new RecordingRepository();
        var tracker = new RecordingChangeTracker(() => commits++);
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider(), tracker);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.BackupDirectoryAsync("agent-1", source.Path, cts.Token));

        Assert.Equal(0, commits);
        Assert.Empty(repository.WrittenManifests);
    }

    [Fact]
    public async Task Chunk_write_failure_never_publishes_manifest_or_checkpoint()
    {
        using var source = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "critical.txt"), "must remain uncommitted");
        var commits = 0;
        var repository = new RecordingRepository { FailChunkWrite = true };
        var engine = new BackupEngine(new WholeFileChunker(), repository, new PassThroughSnapshotProvider(),
            new RecordingChangeTracker(() => commits++));

        await Assert.ThrowsAsync<IOException>(() =>
            engine.BackupDirectoryAsync("agent-1", source.Path, CancellationToken.None));

        Assert.Equal(0, commits);
        Assert.Empty(repository.WrittenManifests);
    }

    [Fact]
    public async Task Snapshot_failure_never_touches_repository()
    {
        using var source = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source.Path, "critical.txt"), "data");
        var repository = new RecordingRepository();
        var engine = new BackupEngine(new WholeFileChunker(), repository, new FailingSnapshotProvider());

        await Assert.ThrowsAsync<IOException>(() =>
            engine.BackupDirectoryAsync("agent-1", source.Path, CancellationToken.None));

        Assert.Empty(repository.StoredChunks);
        Assert.Empty(repository.WrittenManifests);
    }

    private sealed class WholeFileChunker : IChunker
    {
        public string ChunkerId => "test-whole-file";

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            Stream source,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            yield return memory.ToArray();
        }
    }

    private sealed class PassThroughSnapshotProvider : ISnapshotProvider
    {
        public Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SnapshotHandle(sourcePath, sourcePath, false));
        }
    }

    private sealed class FailingSnapshotProvider : ISnapshotProvider
    {
        public Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromException<SnapshotHandle>(new IOException("Injected snapshot failure."));
    }

    private sealed class RecordingChangeTracker(Action onCommit) : IIncrementalChangeTracker
    {
        public Task<IncrementalChangeSet> CaptureAsync(string sourceRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IncrementalChangeSet(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "test",
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    onCommit();
                    return Task.CompletedTask;
                }));
        }
    }

    private sealed class RecordingRepository : IBackupRepository
    {
        public bool FailManifestWrite { get; init; }
        public bool FailChunkWrite { get; init; }
        public Dictionary<string, byte[]> StoredChunks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<BackupManifest> WrittenManifests { get; } = [];
        public string? EncryptionKeyId => "test-key";

        public Task<bool> ContainsChunkAsync(string sha256, CancellationToken cancellationToken) =>
            Task.FromResult(StoredChunks.ContainsKey(sha256));

        public Task PutChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailChunkWrite) throw new IOException("Injected chunk write failure.");
            StoredChunks[sha256] = data.ToArray();
            return Task.CompletedTask;
        }

        public Task WriteManifestAsync(BackupManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailManifestWrite)
                throw new IOException("Injected manifest durability failure.");
            WrittenManifests.Add(manifest);
            return Task.CompletedTask;
        }

        public Task<BackupManifest> ReadManifestAsync(string agentId, string backupId, CancellationToken cancellationToken) =>
            Task.FromResult(WrittenManifests.Single(x => x.AgentId == agentId && x.BackupId == backupId));

        public Task<IReadOnlyList<BackupManifest>> ListManifestsAsync(string agentId, string? sourceRoot, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupManifest>>(WrittenManifests
                .Where(x => x.AgentId == agentId && (sourceRoot is null || string.Equals(x.SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase)))
                .ToArray());

        public Task<IReadOnlyList<BackupManifest>> ListAllManifestsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupManifest>>(WrittenManifests.ToArray());

        public async IAsyncEnumerable<string> EnumerateChunkHashesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var hash in StoredChunks.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return hash;
                await Task.Yield();
            }
        }

        public Task<Stream> OpenChunkReadAsync(string sha256, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(StoredChunks[sha256], writable: false));

        public string DescribeManifestLocation(string agentId, string backupId) => $"memory://{agentId}/{backupId}";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YazmaBackup.Tests", Guid.NewGuid().ToString("N"));
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
