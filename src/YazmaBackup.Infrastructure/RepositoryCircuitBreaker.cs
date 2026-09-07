using System.Text.Json;
using YazmaBackup.Domain;

namespace YazmaBackup.Infrastructure;

public sealed class RepositoryCircuitOpenException(string repositoryId, DateTimeOffset retryAfterUtc)
    : IOException($"Repository circuit '{repositoryId}' is open until {retryAfterUtc:O}.")
{
    public string RepositoryId { get; } = repositoryId;
    public DateTimeOffset RetryAfterUtc { get; } = retryAfterUtc;
}

public sealed class RepositoryCircuitBreaker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];
    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, RepositoryCircuitTelemetry> _state;

    public RepositoryCircuitBreaker(string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        _path = Path.GetFullPath(stateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _state = Load();
    }

    public IReadOnlyList<RepositoryCircuitTelemetry> Snapshot()
    {
        lock (_sync) return _state.Values.OrderBy(x => x.RepositoryId, StringComparer.Ordinal).ToArray();
    }

    public void ThrowIfOpen(string repositoryId, DateTimeOffset nowUtc)
    {
        ValidateRepositoryId(repositoryId);
        lock (_sync)
        {
            if (_state.TryGetValue(repositoryId, out var current) && current.IsOpen(nowUtc))
                throw new RepositoryCircuitOpenException(repositoryId, current.OpenUntilUtc!.Value);
        }
    }

    public RepositoryCircuitTelemetry ReportFailure(string repositoryId, Exception exception, DateTimeOffset nowUtc)
    {
        ValidateRepositoryId(repositoryId);
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
        {
            var previous = _state.GetValueOrDefault(repositoryId);
            var failures = checked((previous?.ConsecutiveFailures ?? 0) + 1);
            var delay = Backoff[Math.Min(failures, Backoff.Length - 1)];
            var error = Sanitize(exception.Message);
            var next = new RepositoryCircuitTelemetry(repositoryId, failures, nowUtc, delay > TimeSpan.Zero ? nowUtc.Add(delay) : null, error);
            _state[repositoryId] = next;
            PersistUnsafe();
            return next;
        }
    }

    public void ReportSuccess(string repositoryId)
    {
        ValidateRepositoryId(repositoryId);
        lock (_sync)
        {
            if (!_state.ContainsKey(repositoryId)) return;
            _state[repositoryId] = new RepositoryCircuitTelemetry(repositoryId, 0, null, null, null);
            PersistUnsafe();
        }
    }

    public static bool IsTransientRepositoryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or TimeoutException;

    private Dictionary<string, RepositoryCircuitTelemetry> Load()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            var rows = JsonSerializer.Deserialize<RepositoryCircuitTelemetry[]>(File.ReadAllText(_path), JsonOptions) ?? [];
            return rows.Where(x => ValidRepositoryId(x.RepositoryId)).ToDictionary(x => x.RepositoryId, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Repository circuit-breaker state is corrupt.", ex);
        }
    }

    private void PersistUnsafe()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, _state.Values.OrderBy(x => x.RepositoryId, StringComparer.Ordinal).ToArray(), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Sanitize(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private static void ValidateRepositoryId(string repositoryId)
    {
        if (!ValidRepositoryId(repositoryId)) throw new ArgumentException("Repository id is invalid.", nameof(repositoryId));
    }

    private static bool ValidRepositoryId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
