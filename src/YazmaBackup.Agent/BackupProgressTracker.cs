using YazmaBackup.Application;

namespace YazmaBackup.Agent;

internal sealed class BackupProgressTracker : IBackupProgressObserver
{
    private readonly object _gate = new();
    private BackupProgressSnapshot _snapshot = new("preparing", 0, 0, 0, 0);

    public void Report(string stage, int filesProcessed, int filesTotal, long logicalBytesProcessed, long logicalBytesTotal)
    {
        lock (_gate)
            _snapshot = new BackupProgressSnapshot(stage, Math.Max(0, filesProcessed), Math.Max(0, filesTotal), Math.Max(0, logicalBytesProcessed), Math.Max(0, logicalBytesTotal));
    }

    public BackupProgressSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }
}

internal readonly record struct BackupProgressSnapshot(string Stage, int FilesProcessed, int FilesTotal, long LogicalBytesProcessed, long LogicalBytesTotal);
