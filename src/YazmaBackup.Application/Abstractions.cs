using YazmaBackup.Domain;

namespace YazmaBackup.Application;

public interface IChunker
{
    string ChunkerId { get; }
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(Stream source, CancellationToken cancellationToken);
}

public interface IBackupRepository
{
    Task<bool> ContainsChunkAsync(string sha256, CancellationToken cancellationToken);
    Task PutChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    Task WriteManifestAsync(BackupManifest manifest, CancellationToken cancellationToken);
    Task<BackupManifest> ReadManifestAsync(string agentId, string backupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupManifest>> ListManifestsAsync(string agentId, string? sourceRoot, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupManifest>> ListAllManifestsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<string> EnumerateChunkHashesAsync(CancellationToken cancellationToken);
    Task<Stream> OpenChunkReadAsync(string sha256, CancellationToken cancellationToken);
    string DescribeManifestLocation(string agentId, string backupId);
    string? EncryptionKeyId { get; }
}

public interface IRepositoryMutationCoordinator
{
    ValueTask<IAsyncDisposable> AcquireMutationLeaseAsync(CancellationToken cancellationToken);
}

public sealed record QuarantinedBackupManifest(
    BackupManifest Manifest,
    DateTimeOffset QuarantinedAtUtc);

public static class RetentionSafetyDefaults
{
    public static readonly TimeSpan ManifestQuarantinePeriod = TimeSpan.FromDays(7);
}

public interface IRetentionSafeRepository : IRepositoryMutationCoordinator
{
    Task QuarantineManifestAsync(
        BackupManifest manifest,
        DateTimeOffset immutableUntilUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantinedBackupManifest>> ListQuarantinedManifestsAsync(
        CancellationToken cancellationToken);

    Task PurgeQuarantinedManifestAsync(
        string agentId,
        string backupId,
        DateTimeOffset expectedQuarantinedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> DeleteChunkIfOlderThanAsync(
        string sha256,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken);
}

public interface ISnapshotProvider
{
    Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken);
}

public interface IIncrementalChangeTracker
{
    Task<IncrementalChangeSet> CaptureAsync(string sourceRoot, CancellationToken cancellationToken);
}

public sealed record IncrementalChangeSet(
    bool CanReuseUnchangedFiles,
    IReadOnlySet<string> ChangedRelativePaths,
    string Mode,
    Func<CancellationToken, Task> CommitAsync);

public interface IThroughputLimiter
{
    Task WaitAsync(int bytes, CancellationToken cancellationToken);
}

public interface ITransferObserver
{
    void OnBytesWritten(int bytes);
}

public interface IBackupProgressObserver
{
    void Report(string stage, int filesProcessed, int filesTotal, long logicalBytesProcessed, long logicalBytesTotal);
}

public sealed class SnapshotHandle : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;

    public SnapshotHandle(string originalPath, string readablePath, bool snapshotBacked, Func<ValueTask>? dispose = null)
    {
        OriginalPath = originalPath;
        ReadablePath = readablePath;
        SnapshotBacked = snapshotBacked;
        _dispose = dispose ?? (() => ValueTask.CompletedTask);
    }

    public string OriginalPath { get; }
    public string ReadablePath { get; }
    public bool SnapshotBacked { get; }
    public ValueTask DisposeAsync() => _dispose();
}

public interface IBackupProtectionGuard
{
    Task<ProtectionAssessment> AssessAsync(
        string readableRoot,
        BackupManifest? previous,
        IncrementalChangeSet changeSet,
        ProtectionPolicy policy,
        CancellationToken cancellationToken);
}

public sealed class RansomwareSuspectedException(ProtectionAssessment assessment)
    : Exception($"Ransomware-like activity detected: {assessment.Reason}")
{
    public ProtectionAssessment Assessment { get; } = assessment;
}
