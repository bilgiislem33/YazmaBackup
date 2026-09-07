using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class PassThroughSnapshotProvider : ISnapshotProvider
{
    public Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SnapshotHandle(sourcePath, sourcePath, snapshotBacked: false));
    }
}
