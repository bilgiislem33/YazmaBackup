using System.Runtime.CompilerServices;
using YazmaBackup.Application;
using YazmaBackup.Domain;

namespace YazmaBackup.Application.Tests;

public sealed class RetentionSafetyTests
{
    [Fact]
    public async Task Referenced_chunk_is_never_deleted_even_when_an_old_manifest_is_removed()
    {
        var now = DateTimeOffset.UtcNow;
        var shared = new BackupChunkRef("shared-hash", 10);
        var old = Manifest("old", now.AddDays(-10), shared);
        var current = Manifest("current", now, shared);
        var repository = new RetentionRepository([old, current]);
        repository.AddChunk("shared-hash", now.AddDays(-30));

        var summary = await new RetentionManager(repository).ApplyAsync(
            "agent-1", "C:\\source", new RetentionPolicy(1, 0, 0, 0, 0), CancellationToken.None);

        Assert.Equal(1, summary.DeletedRestorePoints);
        Assert.Contains("current", repository.Manifests.Select(x => x.BackupId));
        Assert.Contains("shared-hash", repository.Chunks.Keys);
        Assert.DoesNotContain("shared-hash", repository.DeletedChunks);
    }

    [Fact]
    public async Task Chunk_referenced_by_another_agent_is_never_garbage_collected()
    {
        var now = DateTimeOffset.UtcNow;
        var shared = new BackupChunkRef("cross-agent-shared", 5);
        var targetOld = Manifest("target-old", now.AddDays(-20), shared, "agent-1");
        var targetCurrent = Manifest("target-current", now, new BackupChunkRef("current-only", 5), "agent-1");
        var otherAgent = Manifest("other-current", now.AddHours(-1), shared, "agent-2");
        var repository = new RetentionRepository([targetOld, targetCurrent, otherAgent]);
        repository.AddChunk("cross-agent-shared", now.AddDays(-30));
        repository.AddChunk("current-only", now.AddDays(-30));

        await new RetentionManager(repository).ApplyAsync(
            "agent-1", "C:\\source", new RetentionPolicy(1, 0, 0, 0, 0), CancellationToken.None);

        Assert.Contains("cross-agent-shared", repository.Chunks.Keys);
        Assert.DoesNotContain("cross-agent-shared", repository.DeletedChunks);
    }

    [Fact]
    public async Task Recent_orphan_chunk_is_protected_by_grace_period()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new RetentionRepository([Manifest("current", now, new BackupChunkRef("live", 1))]);
        repository.AddChunk("live", now.AddDays(-5));
        repository.AddChunk("recent-orphan", now.AddHours(-1));

        var summary = await new RetentionManager(repository).ApplyAsync(
            "agent-1", "C:\\source", new RetentionPolicy(1, 0, 0, 0, 0), CancellationToken.None);

        Assert.Equal(0, summary.DeletedChunks);
        Assert.Contains("recent-orphan", repository.Chunks.Keys);
    }

    [Fact]
    public async Task Old_unreferenced_chunk_is_deleted_after_grace_period()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new RetentionRepository([Manifest("current", now, new BackupChunkRef("live", 1))]);
        repository.AddChunk("live", now.AddDays(-5));
        repository.AddChunk("old-orphan", now.AddDays(-5));

        var summary = await new RetentionManager(repository).ApplyAsync(
            "agent-1", "C:\\source", new RetentionPolicy(1, 0, 0, 0, 0), CancellationToken.None);

        Assert.Equal(1, summary.DeletedChunks);
        Assert.DoesNotContain("old-orphan", repository.Chunks.Keys);
        Assert.Contains("live", repository.Chunks.Keys);
    }

    [Fact]
    public async Task Immutability_window_prevents_recent_restore_point_deletion()
    {
        var now = DateTimeOffset.UtcNow;
        var recent1 = Manifest("recent-1", now.AddMinutes(-10), new BackupChunkRef("a", 1));
        var recent2 = Manifest("recent-2", now.AddMinutes(-20), new BackupChunkRef("b", 1));
        var old = Manifest("old", now.AddDays(-3), new BackupChunkRef("c", 1));
        var repository = new RetentionRepository([recent1, recent2, old]);

        var summary = await new RetentionManager(repository).ApplyAsync(
            "agent-1", "C:\\source", new RetentionPolicy(1, 0, 0, 0, 24), CancellationToken.None);

        Assert.Contains("recent-1", repository.Manifests.Select(x => x.BackupId));
        Assert.Contains("recent-2", repository.Manifests.Select(x => x.BackupId));
        Assert.DoesNotContain("old", repository.Manifests.Select(x => x.BackupId));
        Assert.Equal(2, summary.KeptRestorePoints);
    }

    [Fact]
    public async Task Invalid_retention_policy_fails_before_any_delete()
    {
        var repository = new RetentionRepository([Manifest("current", DateTimeOffset.UtcNow, new BackupChunkRef("live", 1))]);
        repository.AddChunk("live", DateTimeOffset.UtcNow.AddDays(-5));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new RetentionManager(repository).ApplyAsync(
                "agent-1", "C:\\source", new RetentionPolicy(0, 0, 0, 0, 0), CancellationToken.None));

        Assert.Empty(repository.DeletedManifests);
        Assert.Empty(repository.DeletedChunks);
    }

    private static BackupManifest Manifest(string backupId, DateTimeOffset created, BackupChunkRef chunk, string agentId = "agent-1")
    {
        var file = new BackupFileEntry("file.bin", chunk.Length, created, chunk.Sha256, [chunk]);
        return new BackupManifest("3", backupId, agentId, "C:\\source", created, [file], chunk.Length, chunk.Length, 1, 0, "test", false);
    }

    private sealed class RetentionRepository(IEnumerable<BackupManifest> manifests) : IBackupRepository
    {
        public List<BackupManifest> Manifests { get; } = [.. manifests];
        public Dictionary<string, DateTimeOffset> Chunks { get; } = new(StringComparer.Ordinal);
        public List<string> DeletedManifests { get; } = [];
        public List<string> DeletedChunks { get; } = [];
        public string? EncryptionKeyId => "test-key";

        public void AddChunk(string hash, DateTimeOffset created) => Chunks[hash] = created;

        public Task<IReadOnlyList<BackupManifest>> ListManifestsAsync(string agentId, string? sourceRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Manifests.Where(x => x.AgentId == agentId && (sourceRoot is null || string.Equals(Path.GetFullPath(x.SourceRoot), sourceRoot, StringComparison.OrdinalIgnoreCase))).ToArray();
            return Task.FromResult<IReadOnlyList<BackupManifest>>(result);
        }

        public Task<IReadOnlyList<BackupManifest>> ListAllManifestsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BackupManifest>>(Manifests.ToArray());
        }

        public Task DeleteManifestAsync(string agentId, string backupId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Manifests.RemoveAll(x => x.AgentId == agentId && x.BackupId == backupId);
            DeletedManifests.Add(backupId);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> EnumerateChunkHashesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var hash in Chunks.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return hash;
                await Task.Yield();
            }
        }

        public Task<bool> DeleteChunkIfOlderThanAsync(string sha256, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Chunks.TryGetValue(sha256, out var created) || created >= cutoffUtc) return Task.FromResult(false);
            Chunks.Remove(sha256);
            DeletedChunks.Add(sha256);
            return Task.FromResult(true);
        }

        public Task<bool> ContainsChunkAsync(string sha256, CancellationToken cancellationToken) => Task.FromResult(Chunks.ContainsKey(sha256));
        public Task PutChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { Chunks[sha256] = DateTimeOffset.UtcNow; return Task.CompletedTask; }
        public Task WriteManifestAsync(BackupManifest manifest, CancellationToken cancellationToken) { Manifests.Add(manifest); return Task.CompletedTask; }
        public Task<BackupManifest> ReadManifestAsync(string agentId, string backupId, CancellationToken cancellationToken) => Task.FromResult(Manifests.Single(x => x.AgentId == agentId && x.BackupId == backupId));
        public Task<Stream> OpenChunkReadAsync(string sha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string DescribeManifestLocation(string agentId, string backupId) => $"memory://{agentId}/{backupId}";
    }
}
