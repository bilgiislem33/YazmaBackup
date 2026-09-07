using System.Globalization;

namespace YazmaBackup.ControlPlane;

public sealed class DistributedStateConflictException(string message) : InvalidOperationException(message);

public sealed class DistributedStateCoordinator
{
    private readonly string _lockPath;
    private readonly string _versionPath;

    public DistributedStateCoordinator(string stateRoot)
    {
        var root = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(root);
        _lockPath = Path.Combine(root, "distributed-state.writer.lock");
        _versionPath = Path.Combine(root, "distributed-state.version");
        if (!File.Exists(_versionPath))
            File.WriteAllText(_versionPath, "0", System.Text.Encoding.ASCII);
    }

    public long ReadVersion()
    {
        if (!File.Exists(_versionPath)) return 0;
        var value = File.ReadAllText(_versionPath, System.Text.Encoding.ASCII).Trim();
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : 0;
    }

    public void WriteVersion(long version)
    {
        var temp = _versionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, version.ToString(CultureInfo.InvariantCulture), System.Text.Encoding.ASCII);
        File.Move(temp, _versionPath, overwrite: true);
    }

    public async Task<IAsyncDisposable> AcquireWriteLeaseAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromMilliseconds(25);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                return new WriterLease(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (delay < TimeSpan.FromMilliseconds(500))
                    delay = TimeSpan.FromMilliseconds(Math.Min(500, delay.TotalMilliseconds * 2));
            }
        }
    }

    private sealed class WriterLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
