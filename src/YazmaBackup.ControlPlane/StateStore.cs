using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore : IControlPlaneStore, IResilienceStore, IProductionFabricStore, IBusinessContinuityStore, IDisasterRecoverySessionStore, IMeshCentralFleetStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _path;

    private readonly string _backupPath;

    private readonly DistributedStateCoordinator _distributedState;

    private readonly PostgreSqlStateEngine? _postgresql;

    private readonly bool _transactionalState;

    private readonly bool _directSqlReads;

    private readonly bool _directSqlMutations;

    private long _stateVersion;

    private StateDocument _state;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StateStore(IHostEnvironment environment)
    {
        var root = Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_DIR")
            ?? Path.Combine(environment.ContentRootPath, ".local");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "control-plane-state.json");
        _backupPath = _path + ".bak";
        _distributedState = new DistributedStateCoordinator(root);

        var engine = (Environment.GetEnvironmentVariable("YAZMABACKUP_STATE_ENGINE") ?? "file").Trim().ToLowerInvariant();
        _transactionalState = engine == "postgresql";
        _directSqlReads = _transactionalState &&
            !string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_DIRECT_SQL_READS"), "false", StringComparison.OrdinalIgnoreCase);
        _directSqlMutations = _transactionalState &&
            !string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_DIRECT_SQL_MUTATIONS"), "false", StringComparison.OrdinalIgnoreCase);
        if (engine is not ("file" or "postgresql")) throw new InvalidOperationException("YAZMABACKUP_STATE_ENGINE must be file or postgresql.");

        if (_transactionalState)
        {
            var connectionString = Environment.GetEnvironmentVariable("YAZMABACKUP_POSTGRES_CONNECTION")
                ?? throw new InvalidOperationException("YAZMABACKUP_POSTGRES_CONNECTION is required when YAZMABACKUP_STATE_ENGINE=postgresql.");
            var clusterId = Environment.GetEnvironmentVariable("YAZMABACKUP_CLUSTER_ID") ?? "default";
            _postgresql = new PostgreSqlStateEngine(connectionString, clusterId);
            var bootstrap = Load();
            var bootstrapJson = JsonSerializer.Serialize(bootstrap, JsonOptions);
            var snapshot = _postgresql.LoadOrBootstrap(bootstrapJson);
            _state = JsonSerializer.Deserialize<StateDocument>(snapshot.Json, JsonOptions) ?? throw new InvalidDataException("PostgreSQL Control Plane state is empty.");
            _stateVersion = snapshot.Version;
        }
        else
        {
            _state = Load();
            _stateVersion = _distributedState.ReadVersion();
        }
    }

    public void Dispose() => _gate.Dispose();
}
