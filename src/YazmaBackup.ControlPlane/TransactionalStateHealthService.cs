namespace YazmaBackup.ControlPlane;

public sealed class TransactionalStateHealthService
{
    private readonly string _engine;
    private readonly PostgreSqlStateEngine? _postgresql;

    public TransactionalStateHealthService()
    {
        _engine = (Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_ENGINE") ?? "file").Trim().ToLowerInvariant();
        if (_engine == "postgresql")
        {
            var connection = Environment.GetEnvironmentVariable("YAZMABACKUP_POSTGRES_CONNECTION")
                ?? throw new InvalidOperationException("YAZMABACKUP_POSTGRES_CONNECTION is required.");
            var clusterId = Environment.GetEnvironmentVariable("YAZMABACKUP_CLUSTER_ID") ?? "default";
            _postgresql = new PostgreSqlStateEngine(connection, clusterId);
        }
    }

    public object GetStatus() => _postgresql is null
        ? new { engine = _engine, healthy = true, transactional = false, productionActiveActive = false }
        : new { engine = _engine, healthy = true, transactional = true, productionActiveActive = true, database = _postgresql.GetStatus() };
}
