using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane;

public sealed class TransferTelemetryRegistry
{
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActiveFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HistoryBucket = TimeSpan.FromSeconds(2);
    private const int MaxHistoryBuckets = 1000;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TransferTelemetryPointDto> _latestByCommand = [];
    private readonly SortedDictionary<DateTimeOffset, long> _aggregateHistory = [];

    public void Record(Guid agentId, string machineName, AgentTransferTelemetryRequest request)
    {
        var receivedAtUtc = DateTimeOffset.UtcNow;
        var point = new TransferTelemetryPointDto(
            agentId, machineName, request.CommandId, request.Operation, request.State, request.RepositoryId,
            Math.Max(0, request.BytesTransferred), Math.Max(0, request.BytesPerSecond), request.StartedAtUtc, receivedAtUtc,
            request.Stage, request.LogicalBytesProcessed, request.LogicalBytesTotal, request.FilesProcessed, request.FilesTotal);

        lock (_gate)
        {
            _latestByCommand[request.CommandId] = point;
            PruneUnsafe(receivedAtUtc);
            var bucket = Bucket(receivedAtUtc);
            _aggregateHistory[bucket] = ActiveTotalUnsafe(receivedAtUtc);
            while (_aggregateHistory.Count > MaxHistoryBuckets)
                _aggregateHistory.Remove(_aggregateHistory.Keys.First());
        }
    }

    public TransferTelemetrySnapshotDto Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneUnsafe(now);
            var active = ActiveUnsafe(now).OrderByDescending(x => x.BytesPerSecond).ToArray();
            var history = _aggregateHistory
                .Select(x => new TransferTelemetrySeriesPointDto(x.Key, x.Value))
                .ToArray();
            return new TransferTelemetrySnapshotDto(now, SafeSum(active), active.Length, active, history);
        }
    }

    private TransferTelemetryPointDto[] ActiveUnsafe(DateTimeOffset now) =>
        _latestByCommand.Values
            .Where(x => string.Equals(x.State, "active", StringComparison.OrdinalIgnoreCase) && x.SampledAtUtc >= now.Subtract(ActiveFreshness))
            .ToArray();

    private long ActiveTotalUnsafe(DateTimeOffset atUtc) => SafeSum(ActiveUnsafe(atUtc));

    private static long SafeSum(IEnumerable<TransferTelemetryPointDto> points)
    {
        long total = 0;
        foreach (var point in points)
            total = point.BytesPerSecond > long.MaxValue - total ? long.MaxValue : total + point.BytesPerSecond;
        return total;
    }

    private void PruneUnsafe(DateTimeOffset now)
    {
        var historyCutoff = now.Subtract(HistoryRetention);
        foreach (var stale in _latestByCommand.Where(x => x.Value.SampledAtUtc < historyCutoff).Select(x => x.Key).ToArray())
            _latestByCommand.Remove(stale);
        foreach (var stale in _aggregateHistory.Keys.Where(x => x < historyCutoff).ToArray())
            _aggregateHistory.Remove(stale);
    }

    private static DateTimeOffset Bucket(DateTimeOffset sampledAtUtc)
    {
        var utcTicks = sampledAtUtc.UtcDateTime.Ticks;
        var ticks = utcTicks - utcTicks % HistoryBucket.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
