using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class FallbackSnapshotProvider(ISnapshotProvider primary, ISnapshotProvider fallback) : ISnapshotProvider
{
    public async Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            return await primary.CreateAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return await fallback.CreateAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
    }
}
