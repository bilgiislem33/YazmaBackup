using System.Runtime.Versioning;
using YazmaBackup.Agent;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("YazmaBackup Agent v1.2.0 targets Windows endpoints.");

await RunWindowsAsync(args).ConfigureAwait(false);

[SupportedOSPlatform("windows")]
static async Task RunWindowsAsync(string[] arguments)
{
    AgentPaths.EnsureDirectories();

    if (arguments.Contains("--bootstrap", StringComparer.OrdinalIgnoreCase))
    {
        Bootstrap(arguments);
        Console.WriteLine("YazmaBackup Agent bootstrap configuration stored securely.");
        return;
    }

    if (arguments.Contains("--provision-repository-key", StringComparer.OrdinalIgnoreCase))
    {
        var repositoryId = RequireArgument(arguments, "--repository-id");
        var keyId = RequireArgument(arguments, "--key-id");
        var keyBase64 = Environment.GetEnvironmentVariable("YAZMABACKUP_REPOSITORY_KEY_B64");
        if (string.IsNullOrWhiteSpace(keyBase64))
            throw new ArgumentException("YAZMABACKUP_REPOSITORY_KEY_B64 environment variable is required.");
        var makeActive = arguments.Contains("--make-active", StringComparer.OrdinalIgnoreCase);
        var store = new RepositoryKeyStore();
        store.Provision(repositoryId, keyId, keyBase64, makeActive);
        var summary = store.GetSummary(repositoryId);
        Console.WriteLine($"Repository key provisioned. Repository={summary.RepositoryId}; ActiveKey={summary.ActiveKeyId}; KeyCount={summary.KeyIds.Count}");
        return;
    }

    if (arguments.Contains("--activate-repository-key", StringComparer.OrdinalIgnoreCase))
    {
        var repositoryId = RequireArgument(arguments, "--repository-id");
        var keyId = RequireArgument(arguments, "--key-id");
        var store = new RepositoryKeyStore();
        store.Activate(repositoryId, keyId);
        Console.WriteLine($"Repository active key changed. Repository={repositoryId}; ActiveKey={keyId}");
        return;
    }

    if (arguments.Contains("--remove-repository-key", StringComparer.OrdinalIgnoreCase))
    {
        var repositoryRoot = RequireArgument(arguments, "--repository-root");
        var repositoryId = RequireArgument(arguments, "--repository-id");
        var keyId = RequireArgument(arguments, "--key-id");
        var store = new RepositoryKeyStore();
        await store.RemoveAfterRepositoryValidationAsync(repositoryRoot, repositoryId, keyId, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Repository key removed after repository reference validation. Repository={repositoryId}; KeyId={keyId}");
        return;
    }

    if (arguments.Contains("--repository-key-summary", StringComparer.OrdinalIgnoreCase))
    {
        var repositoryId = RequireArgument(arguments, "--repository-id");
        var summary = new RepositoryKeyStore().GetSummary(repositoryId);
        Console.WriteLine($"Repository={summary.RepositoryId}; ActiveKey={summary.ActiveKeyId}; Keys={string.Join(',', summary.KeyIds)}");
        return;
    }

    var config = AgentConfigStore.Read();
    using var worker = new AgentWorker(config);
    if (arguments.Contains("--service", StringComparer.OrdinalIgnoreCase))
    {
        WindowsServiceHost.Run("YazmaBackupAgent", worker.RunAsync);
        return;
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.WriteLine($"YazmaBackup Agent {AgentWorker.AgentVersion} console mode. Ctrl+C to stop.");
    await worker.RunAsync(shutdown.Token).ConfigureAwait(false);
}

[SupportedOSPlatform("windows")]
static void Bootstrap(string[] arguments)
{
    var server = RequireArgument(arguments, "--server");
    var enrollmentToken = ReadArgument(arguments, "--enrollment-token") ?? Environment.GetEnvironmentVariable("YAZMABACKUP_ENROLLMENT_TOKEN");
    var secretStore = new MachineSecretStore();
    var alreadyEnrolled = AgentIdentityStore.Read() is not null && !string.IsNullOrWhiteSpace(secretStore.Read(AgentPaths.AccessTokenFile));
    if (!alreadyEnrolled && string.IsNullOrWhiteSpace(enrollmentToken))
        throw new ArgumentException("Enrollment token is required for first installation.");
    var allowInsecureHttp = arguments.Contains("--allow-insecure-http", StringComparer.OrdinalIgnoreCase);
    var allowLiveReadFallback = arguments.Contains("--allow-live-read-fallback", StringComparer.OrdinalIgnoreCase);
    var updateKeyPath = ReadArgument(arguments, "--update-public-key") ?? AgentPaths.DefaultUpdatePublicKeyFile;

    var config = new AgentConfig(
        server,
        PollSeconds: 10,
        LeaseRenewSeconds: 30,
        AllowLiveReadFallback: allowLiveReadFallback,
        AllowInsecureHttp: allowInsecureHttp,
        UpdatePublicKeyPath: Path.GetFullPath(updateKeyPath));
    AgentConfigStore.Write(config);

    // R5.19 convergence: a fresh deployment enrollment grant is recovery evidence even when an
    // old complete credential pair exists. Keep it DPAPI-protected until authenticated heartbeat.
    if (!string.IsNullOrWhiteSpace(enrollmentToken))
        secretStore.Write(AgentPaths.EnrollmentTokenFile, enrollmentToken);
}

static string RequireArgument(string[] arguments, string name) =>
    ReadArgument(arguments, name) ?? throw new ArgumentException($"Required argument missing: {name}");

static string? ReadArgument(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            return arguments[index + 1];
    }
    return null;
}
