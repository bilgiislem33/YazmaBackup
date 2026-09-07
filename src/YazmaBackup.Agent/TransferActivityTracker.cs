using System.Diagnostics;
using YazmaBackup.Application;

namespace YazmaBackup.Agent;

internal sealed class TransferActivityTracker : ITransferObserver
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private long _totalBytes;
    private long _lastSampleBytes;
    private long _lastSampleTicks;

    public TransferActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            var nowTicks = _clock.ElapsedTicks;
            var elapsedTicks = nowTicks - _lastSampleTicks;
            var bytes = _totalBytes;
            long bytesPerSecond = 0;
            if (elapsedTicks > 0)
            {
                var seconds = (double)elapsedTicks / Stopwatch.Frequency;
                bytesPerSecond = seconds <= 0 ? 0 : Math.Max(0, (long)Math.Round((bytes - _lastSampleBytes) / seconds));
            }

            _lastSampleBytes = bytes;
            _lastSampleTicks = nowTicks;
            return new TransferActivitySnapshot(bytes, bytesPerSecond);
        }
    }

    public void OnBytesWritten(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate) _totalBytes += bytes;
    }
}

internal readonly record struct TransferActivitySnapshot(long BytesTransferred, long BytesPerSecond);
