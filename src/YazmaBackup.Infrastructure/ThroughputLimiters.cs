using System.Diagnostics;
using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class UnlimitedThroughputLimiter : IThroughputLimiter
{
    public Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class TokenBucketThroughputLimiter : IThroughputLimiter, IDisposable
{
    private readonly Func<long> _bytesPerSecond;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private long _lastTicks;

    public TokenBucketThroughputLimiter(Func<long> bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond ?? throw new ArgumentNullException(nameof(bytesPerSecond));
        _lastTicks = _clock.ElapsedTicks;
    }

    public async Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var rate = _bytesPerSecond();
                if (rate <= 0) return;
                var now = _clock.ElapsedTicks;
                var elapsedSeconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _lastTicks = now;
                var capacity = Math.Max(rate, bytes);
                _tokens = Math.Min(capacity, _tokens + elapsedSeconds * rate);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                var missing = bytes - _tokens;
                var delay = TimeSpan.FromSeconds(Math.Min(1.0, missing / rate));
                _tokens = 0;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
