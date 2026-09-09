using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using YazmaBackup.Application;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using YazmaBackup.Infrastructure;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class AgentWorker : IDisposable
{
    private static readonly JsonSerializerOptions CommandResultJsonOptions = new(JsonSerializerDefaults.Web);
    public const string AgentVersion = "1.2.0";
    private static readonly string[] AgentCapabilities =
    [
        "aes256-gcm-repository-v1",
        "backup-cdc-gear-v1",
        "central-scheduler-v1",
        "command-lease-v1",
        "machine-dpapi-v1",
        "point-in-time-restore-v1",
        "ransomware-preflight-v1",
        "protection-lock-dpapi-v1",
        "remote-browse-v1",
        "remote-restore-integrity-v1",
        "remote-restore-point-browser-v1",
        "repository-keyring-v1",
        "rsa3072-key-wrap-v1",
        "repository-scrub-v1",
        "repository-circuit-breaker-v1",
        "retention-gfs-v1",
        "signed-update-stage-ecdsa-p256-v1",
        "usn-incremental-v1",
        "user-aware-bandwidth-v1",
        "live-transfer-telemetry-v1",
        "pilot-readiness-probe-v1",
        "deep-validation-matrix-v1",
        "granular-restore-v1",
        "restore-explorer-v1",
        "backup-progress-telemetry-v1",
        "restore-sandbox-v1",
        "safe-self-healing-v1",
        "windows-native-service-v1",
        "windows-vss-crash-consistent-v1"
    ];
    private readonly AgentConfig _config;
    private readonly HttpClient _http;
    private readonly MachineSecretStore _secretStore = new();
    private readonly RepositoryKeyStore _repositoryKeys = new();
    private readonly NasCredentialStore _nasCredentials = new();
    private readonly AgentKeyExchangeStore _keyExchange = new();
    private readonly ProtectionLockStore _protectionLock = new();
    private readonly RepositoryCircuitBreaker _repositoryCircuit = new(AgentPaths.RepositoryCircuitStateFile);
    private AgentIdentity? _identity;
    private string? _accessToken;

    public AgentWorker(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.Server), Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AgentPaths.EnsureDirectories();
        CommandExecutionJournal.Cleanup(TimeSpan.FromDays(7));
        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);
        var identity = _identity ?? throw new InvalidOperationException("Agent identity is unavailable after enrollment.");
        AgentLog.Info("agent-started", "YazmaBackup Agent started.", new { identity.AgentId, version = AgentVersion, server = _config.Server });

        var transientControlPlaneFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatAsync(cancellationToken).ConfigureAwait(false);
                var command = await GetNextCommandAsync(cancellationToken).ConfigureAwait(false);
                if (command is not null)
                    await ExecuteLeasedCommandAsync(command, cancellationToken).ConfigureAwait(false);
                transientControlPlaneFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                transientControlPlaneFailures++;
                var baseSeconds = Math.Max(20, _config.PollSeconds);
                var delaySeconds = Math.Min(120, baseSeconds * (1 << Math.Min(transientControlPlaneFailures - 1, 2)));
                AgentLog.Warning("control-plane-temporarily-unavailable", "Control Plane or reverse proxy is temporarily unavailable; Agent will retry with bounded backoff.", new
                {
                    statusCode = (int?)ex.StatusCode,
                    transientControlPlaneFailures,
                    retryAfterSeconds = delaySeconds
                });
                await DelaySafeAsync(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                AgentLog.Warning("agent-authentication-failed", "Control Plane rejected the active Agent credential; protected re-enrollment recovery will be attempted when a fresh bootstrap grant is available.");
                if (await TryRecoverAuthenticationAsync(cancellationToken).ConfigureAwait(false))
                    continue;
                await DelaySafeAsync(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AgentLog.Error("agent-loop-error", ex.Message, new { exceptionType = ex.GetType().FullName });
                await DelaySafeAsync(TimeSpan.FromSeconds(_config.PollSeconds), cancellationToken).ConfigureAwait(false);
            }

            await DelaySafeAsync(TimeSpan.FromSeconds(_config.PollSeconds), cancellationToken).ConfigureAwait(false);
        }

        AgentLog.Info("agent-stopped", "YazmaBackup Agent stopped.");
    }

    public void Dispose() => _http.Dispose();

    private async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        _identity = AgentIdentityStore.Read();
        _accessToken = _secretStore.Read(AgentPaths.AccessTokenFile);
        if (_identity is not null && !string.IsNullOrWhiteSpace(_accessToken)) return;

        var enrollmentToken = Environment.GetEnvironmentVariable("YAZMABACKUP_ENROLLMENT_TOKEN");
        if (string.IsNullOrWhiteSpace(enrollmentToken))
            enrollmentToken = _secretStore.Read(AgentPaths.EnrollmentTokenFile);
        if (string.IsNullOrWhiteSpace(enrollmentToken))
            throw new InvalidOperationException("Agent is not enrolled and no protected bootstrap enrollment token is available.");

        await EnrollWithTokenAsync(enrollmentToken, "agent-enrolled", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRecoverAuthenticationAsync(CancellationToken cancellationToken)
    {
        var enrollmentToken = _secretStore.Read(AgentPaths.EnrollmentTokenFile);
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            AgentLog.Error("agent-reenrollment-unavailable", "No protected fresh enrollment grant is available. A new MeshCentral Agent deployment is required.");
            return false;
        }

        try
        {
            await EnrollWithTokenAsync(enrollmentToken, "agent-reenrolled", cancellationToken).ConfigureAwait(false);
            AgentLog.Info("agent-authentication-recovered", "Agent credential recovery completed; authenticated heartbeat will verify the replacement credential.");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            MachineSecretStore.Delete(AgentPaths.EnrollmentTokenFile);
            AgentLog.Error("agent-reenrollment-rejected", "Protected enrollment grant was rejected or expired. A new MeshCentral Agent deployment is required.");
            return false;
        }
    }

    private async Task EnrollWithTokenAsync(string enrollmentToken, string eventName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/register")
        {
            Content = JsonContent.Create(new RegisterAgentRequest(Environment.MachineName, RuntimeInformation.OSDescription, AgentVersion, AgentCapabilities, _keyExchange.GetOrCreatePublicKeyPem()))
        };
        request.Headers.Add("X-YazmaBackup-Enrollment", enrollmentToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("Enrollment grant rejected.", null, HttpStatusCode.Unauthorized);
        response.EnsureSuccessStatusCode();

        var registered = await response.Content.ReadFromJsonAsync<RegisterAgentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Registration response is empty.");

        var replacementIdentity = new AgentIdentity(registered.AgentId);
        AgentIdentityStore.Write(replacementIdentity);
        _secretStore.Write(AgentPaths.AccessTokenFile, registered.AccessToken);
        _identity = replacementIdentity;
        _accessToken = registered.AccessToken;
        AgentLog.Info(eventName, "Agent enrollment credential was issued and stored securely.", new { registered.AgentId });
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var (identity, token) = ActiveCredentials();
        using var request = Authorized(HttpMethod.Post, "/api/v1/agent/heartbeat", identity.AgentId, token,
            JsonContent.Create(new HeartbeatRequest(Environment.MachineName, RuntimeInformation.OSDescription, AgentVersion, AgentCapabilities, DateTimeOffset.UtcNow, _keyExchange.GetOrCreatePublicKeyPem(), _protectionLock.Telemetry(), _repositoryCircuit.Snapshot())));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Authenticated heartbeat is the proof point that the active/replacement credential works.
        MachineSecretStore.Delete(AgentPaths.EnrollmentTokenFile);
    }

    private async Task<AgentCommandDto?> GetNextCommandAsync(CancellationToken cancellationToken)
    {
        var (identity, token) = ActiveCredentials();
        using var request = Authorized(HttpMethod.Get, "/api/v1/agent/commands/next", identity.AgentId, token, null);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentCommandDto>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteLeasedCommandAsync(AgentCommandDto command, CancellationToken serviceCancellationToken)
    {
        var cached = CommandExecutionJournal.Read(command.CommandId);
        if (cached is not null)
        {
            AgentLog.Info("command-result-replay", "Replaying cached command result.", new { command.CommandId, command.AttemptCount });
            await PostResultAsync(command.CommandId, command.LeaseId, cached.Succeeded, cached.ResultJson, cached.Error, serviceCancellationToken).ConfigureAwait(false);
            return;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
        using var leaseStop = new CancellationTokenSource();
        var leaseState = new LeaseState(command.LeaseExpiresAtUtc);
        var leaseTask = RenewLeaseLoopAsync(command.CommandId, command.LeaseId, leaseState, operationCancellation, leaseStop.Token);
        CachedCommandResult? result = null;
        try
        {
            AgentLog.Info("command-started", "Command execution started.", new { command.CommandId, command.Type, command.AttemptCount });
            var resultJson = await RunCommandAsync(command, operationCancellation.Token).ConfigureAwait(false);
            result = new CachedCommandResult(command.CommandId, true, resultJson, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (serviceCancellationToken.IsCancellationRequested || leaseState.Lost)
        {
            AgentLog.Warning("command-interrupted", "Command execution was interrupted and will remain retryable.", new { command.CommandId, leaseLost = leaseState.Lost });
            return;
        }
        catch (Exception ex)
        {
            var error = SanitizeError(ex);
            var context = DescribeCommandContext(command);
            AgentLog.Error("command-failed", "Command execution failed.", new
            {
                command.CommandId,
                command.Type,
                exceptionType = ex.GetType().FullName,
                error,
                context.SourcePath,
                context.RepositoryRoot,
                context.RepositoryId
            });
            result = new CachedCommandResult(command.CommandId, false, "{}", error, DateTimeOffset.UtcNow);
        }
        finally
        {
            leaseStop.Cancel();
            try { await leaseTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (result is null) return;
        CommandExecutionJournal.Write(result);
        await PostResultAsync(command.CommandId, command.LeaseId, result.Succeeded, result.ResultJson, result.Error, serviceCancellationToken).ConfigureAwait(false);
        AgentLog.Info("command-completed", "Command result committed to Control Plane.", new { command.CommandId, result.Succeeded, error = result.Succeeded ? null : result.Error });
    }

    private async Task<string> RunCommandAsync(AgentCommandDto command, CancellationToken cancellationToken)
    {
        var repositoryId = TryGetRepositoryId(command);
        if (repositoryId is null) return await RunCommandCoreAsync(command, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        // A real NAS access test is the recovery probe for an open circuit. Blocking that
        // probe would leave a repaired repository unusable until the full backoff expires.
        // All data operations remain fail-fast while the circuit is open.
        if (!CanProbeOpenRepositoryCircuit(command.Type))
            _repositoryCircuit.ThrowIfOpen(repositoryId, now);
        try
        {
            var repositoryRoot = TryGetRepositoryRoot(command);
            using var nasConnection = repositoryRoot is not null
                ? NasConnectionScope.ConnectIfConfigured(repositoryRoot, _nasCredentials.Read(repositoryId))
                : null;
            var result = await RunCommandCoreAsync(command, cancellationToken).ConfigureAwait(false);
            _repositoryCircuit.ReportSuccess(repositoryId);
            return result;
        }
        catch (Exception ex) when (RepositoryCircuitBreaker.IsTransientRepositoryFailure(ex))
        {
            var circuit = _repositoryCircuit.ReportFailure(repositoryId, ex, DateTimeOffset.UtcNow);
            AgentLog.Warning("repository-circuit-failure", "Repository operation failed and circuit-breaker state was updated.", new { repositoryId, circuit.ConsecutiveFailures, circuit.OpenUntilUtc, error = circuit.LastError });
            throw;
        }
    }

    private static bool CanProbeOpenRepositoryCircuit(string commandType) =>
        string.Equals(commandType, "TestNasAccess", StringComparison.Ordinal);

    private static (string? SourcePath, string? RepositoryRoot, string? RepositoryId) DescribeCommandContext(AgentCommandDto command)
    {
        try
        {
            if (command.Type == "BackupPath")
            {
                var payload = JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson);
                return (payload?.Path, payload?.RepositoryRoot, payload?.RepositoryId);
            }

            return (null, TryGetRepositoryRoot(command), TryGetRepositoryId(command));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? TryGetRepositoryId(AgentCommandDto command)
    {
        try
        {
            return command.Type switch
            {
                "BackupPath" => JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson)?.RepositoryId,
                "RestoreBackup" => JsonSerializer.Deserialize<RestorePayload>(command.PayloadJson)?.RepositoryId,
                "RestorePointInTime" => JsonSerializer.Deserialize<RestorePointInTimePayload>(command.PayloadJson)?.RepositoryId,
                "ScrubRepository" => JsonSerializer.Deserialize<ScrubRepositoryPayload>(command.PayloadJson)?.RepositoryId,
                "RestoreDrill" => JsonSerializer.Deserialize<RestoreDrillPayload>(command.PayloadJson)?.RepositoryId,
                "RepositoryHealthScan" => JsonSerializer.Deserialize<RepositoryHealthScanPayload>(command.PayloadJson)?.RepositoryId,
                "RemoveRepositoryKey" => JsonSerializer.Deserialize<RemoveRepositoryKeyPayload>(command.PayloadJson)?.RepositoryId,
                "ListRestorePoints" => JsonSerializer.Deserialize<ListRestorePointsPayload>(command.PayloadJson)?.RepositoryId,
                "ListRestoreEntries" => JsonSerializer.Deserialize<ListRestoreEntriesPayload>(command.PayloadJson)?.RepositoryId,
                "PilotReadinessProbe" => JsonSerializer.Deserialize<PilotReadinessProbePayload>(command.PayloadJson)?.RepositoryId,
                "GranularRestore" => JsonSerializer.Deserialize<GranularRestorePayload>(command.PayloadJson)?.RepositoryId,
                "RestoreSandbox" => JsonSerializer.Deserialize<RestoreSandboxPayload>(command.PayloadJson)?.RepositoryId,
                "DeepValidationProbe" => JsonSerializer.Deserialize<DeepValidationProbePayload>(command.PayloadJson)?.RepositoryId,
                "TestNasAccess" => JsonSerializer.Deserialize<TestNasAccessPayload>(command.PayloadJson)?.RepositoryId,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? TryGetRepositoryRoot(AgentCommandDto command)
    {
        try
        {
            return command.Type switch
            {
                "BackupPath" => JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson)?.RepositoryRoot,
                "RestoreBackup" => JsonSerializer.Deserialize<RestorePayload>(command.PayloadJson)?.RepositoryRoot,
                "RestorePointInTime" => JsonSerializer.Deserialize<RestorePointInTimePayload>(command.PayloadJson)?.RepositoryRoot,
                "ScrubRepository" => JsonSerializer.Deserialize<ScrubRepositoryPayload>(command.PayloadJson)?.RepositoryRoot,
                "RestoreDrill" => JsonSerializer.Deserialize<RestoreDrillPayload>(command.PayloadJson)?.RepositoryRoot,
                "RepositoryHealthScan" => JsonSerializer.Deserialize<RepositoryHealthScanPayload>(command.PayloadJson)?.RepositoryRoot,
                "RemoveRepositoryKey" => JsonSerializer.Deserialize<RemoveRepositoryKeyPayload>(command.PayloadJson)?.RepositoryRoot,
                "ListRestorePoints" => JsonSerializer.Deserialize<ListRestorePointsPayload>(command.PayloadJson)?.RepositoryRoot,
                "ListRestoreEntries" => JsonSerializer.Deserialize<ListRestoreEntriesPayload>(command.PayloadJson)?.RepositoryRoot,
                "PilotReadinessProbe" => JsonSerializer.Deserialize<PilotReadinessProbePayload>(command.PayloadJson)?.RepositoryRoot,
                "DeepValidationProbe" => JsonSerializer.Deserialize<DeepValidationProbePayload>(command.PayloadJson)?.RepositoryRoot,
                "GranularRestore" => JsonSerializer.Deserialize<GranularRestorePayload>(command.PayloadJson)?.RepositoryRoot,
                "RestoreSandbox" => JsonSerializer.Deserialize<RestoreSandboxPayload>(command.PayloadJson)?.RepositoryRoot,
                "TestNasAccess" => JsonSerializer.Deserialize<TestNasAccessPayload>(command.PayloadJson)?.RepositoryRoot,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private async Task<string> RunCommandCoreAsync(AgentCommandDto command, CancellationToken cancellationToken)
    {
        var identity = _identity ?? throw new InvalidOperationException("Agent identity is unavailable.");
        if (command.Type == "BrowsePath")
        {
            var payload = JsonSerializer.Deserialize<BrowsePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid browse payload.");
            return JsonSerializer.Serialize(Browse(payload.Path), CommandResultJsonOptions);
        }


        if (command.Type == "DeepValidationProbe")
        {
            var payload = JsonSerializer.Deserialize<DeepValidationProbePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid deep-validation payload.");
            var probe = new DeepValidationProbe(_repositoryKeys, _repositoryCircuit);
            var result = await probe.RunAsync(payload, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, CommandResultJsonOptions);
        }

        if (command.Type == "GranularRestore")
        {
            var payload = JsonSerializer.Deserialize<GranularRestorePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid granular-restore payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var restore = new RestoreEngine(repository);
            var summary = await restore.RestoreSelectedAsync(identity.AgentId.ToString("D"), payload.BackupId, payload.DestinationRoot, payload.IncludePaths, payload.OverwriteExisting, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RestoreResultDto(payload.BackupId, summary.RestoredFiles + summary.AlreadyPresentFiles, summary.RestoredBytes, Path.GetFullPath(payload.DestinationRoot)), CommandResultJsonOptions);
        }

        if (command.Type == "RestoreSandbox")
        {
            var payload = JsonSerializer.Deserialize<RestoreSandboxPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid restore-sandbox payload.");
            var baseRoot = Path.GetFullPath(payload.SandboxRoot);
            Directory.CreateDirectory(baseRoot);
            var runRoot = Path.Combine(baseRoot, $"yb-sandbox-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var restore = new RestoreEngine(repository);
            var summary = await restore.RestoreSandboxAsync(identity.AgentId.ToString("D"), payload.BackupId, runRoot, payload.MaxFiles, payload.MaxBytes, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RestoreSandboxResultDto(payload.BackupId, runRoot, summary.RestoredFiles + summary.AlreadyPresentFiles, summary.RestoredBytes, "verified", DateTimeOffset.UtcNow), CommandResultJsonOptions);
        }

        if (command.Type == "SelfHealingDiagnose")
        {
            var payload = JsonSerializer.Deserialize<SelfHealingDiagnosePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid self-healing diagnosis payload.");
            var result = new SelfHealingService(_repositoryCircuit).Diagnose(payload);
            return JsonSerializer.Serialize(result, CommandResultJsonOptions);
        }

        if (command.Type == "SelfHealingApply")
        {
            var payload = JsonSerializer.Deserialize<SelfHealingApplyPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid self-healing apply payload.");
            var result = new SelfHealingService(_repositoryCircuit).Apply(payload);
            return JsonSerializer.Serialize(result, CommandResultJsonOptions);
        }

        if (command.Type == "BackupPath")
        {
            var payload = JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid backup payload.");
            ValidateBackupPayload(payload);
            var activeLock = _protectionLock.Read();
            if (activeLock is not null)
                throw new InvalidOperationException($"Backup protection is locked by incident {activeLock.IncidentId:D}. Clear the incident before starting a new backup.");
            EnsureRepositoryOutsideSource(payload.Path, payload.RepositoryRoot);
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var idleThreshold = TimeSpan.FromSeconds(payload.UserIdleThresholdSeconds);
            var transferTracker = new TransferActivityTracker();
            var backupProgress = new BackupProgressTracker();
            using var rateLimiter = new TokenBucketThroughputLimiter(() =>
                WindowsSessionActivityProbe.IsUserActive(idleThreshold) ? payload.ActiveBytesPerSecond : payload.IdleBytesPerSecond);
            var repository = new FileSystemBackupRepository(
                payload.RepositoryRoot, payload.RepositoryId, crypto, rateLimiter,
                transferObserver: transferTracker);
            ISnapshotProvider snapshotProvider;
            if (!payload.RequireSnapshot)
            {
                snapshotProvider = new PassThroughSnapshotProvider();
            }
            else if (_config.AllowLiveReadFallback)
            {
                snapshotProvider = new FallbackSnapshotProvider(new WindowsVssSnapshotProvider(), new PassThroughSnapshotProvider());
            }
            else
            {
                snapshotProvider = new WindowsVssSnapshotProvider();
            }

            var tracker = new WindowsUsnJournalChangeTracker(AgentPaths.ChangeTrackingDirectory);
            var protection = payload.Protection ?? new ProtectionPolicy();
            var engine = new BackupEngine(new ContentDefinedChunker(), repository, snapshotProvider, tracker, new RansomwareProtectionGuard(), backupProgress);
            BackupManifest manifest;
            var transferStartedAtUtc = DateTimeOffset.UtcNow;
            using var telemetryStop = new CancellationTokenSource();
            var telemetryTask = PublishTransferTelemetryLoopAsync(command.CommandId, payload.RepositoryId, transferTracker, backupProgress, transferStartedAtUtc, telemetryStop.Token);
            var terminalTransferState = "failed";
            try
            {
                manifest = await engine.BackupDirectoryAsync(identity.AgentId.ToString("D"), payload.Path, protection, cancellationToken).ConfigureAwait(false);
                terminalTransferState = "completed";
            }
            catch (RansomwareSuspectedException ex) when (protection.Enabled && protection.AutoLockOnDetection)
            {
                var incident = _protectionLock.Lock(ex.Assessment);
                AgentLog.Error("protection-locked", "Ransomware-like activity blocked a backup commit.", new
                {
                    incident.IncidentId,
                    incident.TriggeredAtUtc,
                    incident.Reason,
                    ex.Assessment.ChangedFileCount,
                    ex.Assessment.ExtensionChangeCount,
                    ex.Assessment.HighEntropySampleCount
                });
                throw new InvalidDataException($"Protection incident {incident.IncidentId:D}: {incident.Reason}. Backup commit blocked.", ex);
            }
            finally
            {
                telemetryStop.Cancel();
                try { await telemetryTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                await PublishTransferTelemetryAsync(command.CommandId, payload.RepositoryId, terminalTransferState, transferTracker, backupProgress, transferStartedAtUtc, CancellationToken.None).ConfigureAwait(false);
            }
            var retention = new RetentionManager(repository);
            var retentionSummary = await retention.ApplyAsync(identity.AgentId.ToString("D"), payload.Path, payload.Retention, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new BackupResultDto(
                manifest.BackupId, manifest.Files.Count, manifest.LogicalBytes, manifest.UploadedBytes,
                manifest.NewChunks, manifest.ReusedChunks, manifest.ReusedFiles,
                repository.DescribeManifestLocation(manifest.AgentId, manifest.BackupId), manifest.SnapshotBacked,
                manifest.IncrementalMode, manifest.EncryptionKeyId,
                retentionSummary.DeletedRestorePoints, retentionSummary.DeletedChunks), CommandResultJsonOptions);
        }

        if (command.Type == "ListRestorePoints")
        {
            var payload = JsonSerializer.Deserialize<ListRestorePointsPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid restore-point list payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var points = (await repository.ListManifestsAsync(identity.AgentId.ToString("D"), payload.SourceRoot, cancellationToken).ConfigureAwait(false))
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(500)
                .Select(x => new RestorePointSummaryDto(x.BackupId, x.CreatedAtUtc, x.LogicalBytes, x.Files.Count, x.SnapshotBacked, x.IncrementalMode))
                .ToArray();
            return JsonSerializer.Serialize(new ListRestorePointsResultDto(Path.GetFullPath(payload.SourceRoot), points), CommandResultJsonOptions);
        }

        if (command.Type == "ListRestoreEntries")
        {
            var payload = JsonSerializer.Deserialize<ListRestoreEntriesPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid restore-entry list payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var manifest = await repository.ReadManifestAsync(identity.AgentId.ToString("D"), payload.BackupId, cancellationToken).ConfigureAwait(false);
            var prefix = NormalizeRestorePrefix(payload.Prefix);
            var entries = BuildRestoreEntries(manifest, prefix);
            return JsonSerializer.Serialize(new ListRestoreEntriesResultDto(payload.BackupId, prefix, entries), CommandResultJsonOptions);
        }

        if (command.Type == "RestoreBackup")
        {
            var payload = JsonSerializer.Deserialize<RestorePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid restore payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var restore = new RestoreEngine(repository);
            var summary = await restore.RestoreAsync(identity.AgentId.ToString("D"), payload.BackupId, payload.DestinationRoot, payload.OverwriteExisting, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RestoreResultDto(payload.BackupId, summary.RestoredFiles + summary.AlreadyPresentFiles, summary.RestoredBytes, Path.GetFullPath(payload.DestinationRoot)), CommandResultJsonOptions);
        }

        if (command.Type == "RestorePointInTime")
        {
            var payload = JsonSerializer.Deserialize<RestorePointInTimePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid point-in-time restore payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var restorePoint = (await repository.ListManifestsAsync(identity.AgentId.ToString("D"), payload.SourceRoot, cancellationToken).ConfigureAwait(false))
                .Where(m => m.CreatedAtUtc <= payload.RestorePointUtc)
                .OrderByDescending(m => m.CreatedAtUtc)
                .FirstOrDefault()
                ?? throw new KeyNotFoundException("No restore point exists at or before the requested UTC timestamp.");
            var restore = new RestoreEngine(repository);
            var summary = await restore.RestoreAsync(identity.AgentId.ToString("D"), restorePoint.BackupId, payload.DestinationRoot, payload.OverwriteExisting, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new RestoreResultDto(restorePoint.BackupId, summary.RestoredFiles + summary.AlreadyPresentFiles, summary.RestoredBytes, Path.GetFullPath(payload.DestinationRoot)), CommandResultJsonOptions);
        }

        if (command.Type == "ScrubRepository")
        {
            var payload = JsonSerializer.Deserialize<ScrubRepositoryPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid repository scrub payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(
                payload.RepositoryRoot,
                payload.RepositoryId,
                crypto,
                limiter: null,
                allowLegacyInitialization: payload.MigrateLegacyPlaintext);
            var scrub = await repository.ScrubAsync(payload.MigrateLegacyPlaintext, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new ScrubResultDto(scrub.VerifiedChunks, scrub.MigratedChunks, scrub.VerifiedManifests, scrub.VerifiedLogicalBytes), CommandResultJsonOptions);
        }

        if (command.Type == "RestoreDrill")
        {
            var payload = JsonSerializer.Deserialize<RestoreDrillPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid restore drill payload.");
            using var crypto = _repositoryKeys.OpenCryptoContext(payload.RepositoryId);
            var repository = new FileSystemBackupRepository(payload.RepositoryRoot, payload.RepositoryId, crypto);
            var latest = (await repository.ListManifestsAsync(identity.AgentId.ToString("D"), payload.SourceRoot, cancellationToken).ConfigureAwait(false))
                .OrderByDescending(m => m.CreatedAtUtc)
                .FirstOrDefault()
                ?? throw new KeyNotFoundException("No restore point exists for restore drill.");
            var destination = Path.Combine(AgentPaths.RestoreDrillDirectory, command.CommandId.ToString("N"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            int verifiedFiles = 0;
            long verifiedBytes = 0;
            var cleanupSucceeded = true;
            try
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.CreateDirectory(destination);
                var restore = new RestoreEngine(repository);
                var summary = await restore.RestoreAsync(identity.AgentId.ToString("D"), latest.BackupId, destination, overwriteExisting: false, cancellationToken).ConfigureAwait(false);
                verifiedFiles = summary.RestoredFiles + summary.AlreadyPresentFiles;
                verifiedBytes = summary.RestoredBytes;
            }
            finally
            {
                started.Stop();
                try
                {
                    if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                }
                catch (Exception ex)
                {
                    cleanupSucceeded = false;
                    AgentLog.Warning("restore-drill-cleanup-failed", "Restore drill temporary directory cleanup failed.", new { destination, error = ex.Message });
                }
            }
            if (!cleanupSucceeded)
                throw new IOException($"Restore drill verification succeeded but temporary data cleanup failed: {destination}");
            return JsonSerializer.Serialize(new RestoreDrillResultDto(latest.BackupId, verifiedFiles, verifiedBytes, started.ElapsedMilliseconds, cleanupSucceeded), CommandResultJsonOptions);
        }

        if (command.Type == "RepositoryHealthScan")
        {
            var payload = JsonSerializer.Deserialize<RepositoryHealthScanPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid repository health payload.");
            return JsonSerializer.Serialize(RepositoryHealthScanner.Scan(payload), CommandResultJsonOptions);
        }

        if (command.Type == "PilotReadinessProbe")
        {
            var payload = JsonSerializer.Deserialize<PilotReadinessProbePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid pilot readiness payload.");
            var probe = new PilotReadinessProbe(_repositoryKeys);
            return JsonSerializer.Serialize(probe.Run(payload), CommandResultJsonOptions);
        }

        if (command.Type == "ProvisionAndTestNasCredential")
        {
            var payload = JsonSerializer.Deserialize<WrappedProvisionAndTestNasCredentialPayload>(command.PayloadJson)
                ?? throw new InvalidDataException("Invalid wrapped NAS provision-and-test payload.");

            var clear = _keyExchange.UnwrapSecret(payload.WrappedCredentialBase64, 512);
            try
            {
                var secret = JsonSerializer.Deserialize<NasCredentialSecret>(clear)
                    ?? throw new InvalidDataException("NAS credential could not be decoded.");

                var attempt = NasConnectionScope.ConnectForTest(payload.RepositoryRoot, secret);
                if (!attempt.Succeeded)
                {
                    return JsonSerializer.Serialize(new
                    {
                        succeeded = false,
                        repositoryId = payload.RepositoryId,
                        repositoryRoot = payload.RepositoryRoot,
                        credentialConfigured = false,
                        directoryReadable = false,
                        writeProbeSucceeded = false,
                        windowsError = attempt.ErrorCode,
                        shareRoot = attempt.ShareRoot,
                        message = $"SMB kimlik doğrulama/oturum açma başarısız. WindowsError={attempt.ErrorCode}; {attempt.Message}"
                    }, CommandResultJsonOptions);
                }

                using (attempt.Scope)
                {
                    var readable = Directory.Exists(payload.RepositoryRoot);
                    if (!readable)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            succeeded = false,
                            repositoryId = payload.RepositoryId,
                            repositoryRoot = payload.RepositoryRoot,
                            credentialConfigured = false,
                            directoryReadable = false,
                            writeProbeSucceeded = false,
                            windowsError = attempt.ErrorCode,
                            shareRoot = attempt.ShareRoot,
                            message = "SMB oturumu açıldı ancak repository yolu bulunamadı veya paylaşım izni yok."
                        }, CommandResultJsonOptions);
                    }

                    var probe = Path.Combine(payload.RepositoryRoot, ".yazmabackup-access-probe-" + Guid.NewGuid().ToString("N"));
                    var writable = false;
                    try
                    {
                        Directory.CreateDirectory(probe);
                        writable = Directory.Exists(probe);
                    }
                    finally
                    {
                        if (Directory.Exists(probe)) Directory.Delete(probe, recursive: false);
                    }

                    if (!writable)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            succeeded = false,
                            repositoryId = payload.RepositoryId,
                            repositoryRoot = payload.RepositoryRoot,
                            credentialConfigured = false,
                            directoryReadable = true,
                            writeProbeSucceeded = false,
                            windowsError = attempt.ErrorCode,
                            shareRoot = attempt.ShareRoot,
                            message = "NAS okunabiliyor ancak yazma testi başarısız."
                        }, CommandResultJsonOptions);
                    }
                }

                _nasCredentials.Provision(payload.RepositoryId, secret);
                return JsonSerializer.Serialize(new
                {
                    succeeded = true,
                    repositoryId = payload.RepositoryId,
                    repositoryRoot = payload.RepositoryRoot,
                    credentialConfigured = true,
                    directoryReadable = true,
                    writeProbeSucceeded = true,
                    windowsError = attempt.ErrorCode,
                    shareRoot = attempt.ShareRoot,
                    message = "NAS kimlik doğrulama, okuma ve yazma testi başarılı. Kimlik bilgisi DPAPI ile kaydedildi."
                }, CommandResultJsonOptions);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear);
            }
        }

        if (command.Type == "ProvisionNasCredential")
        {
            var payload = JsonSerializer.Deserialize<WrappedNasCredentialPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid wrapped NAS credential payload.");
            var clear = _keyExchange.UnwrapSecret(payload.WrappedCredentialBase64, 512);
            try
            {
                var secret = JsonSerializer.Deserialize<NasCredentialSecret>(clear) ?? throw new InvalidDataException("NAS credential could not be decoded.");
                _nasCredentials.Provision(payload.RepositoryId, secret);
                return JsonSerializer.Serialize(new { repositoryId = payload.RepositoryId, credentialConfigured = true }, CommandResultJsonOptions);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear); }
        }

        if (command.Type == "TestNasAccess")
        {
            var payload = JsonSerializer.Deserialize<TestNasAccessPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid NAS access test payload.");
            var configured = NasCredentialStore.Exists(payload.RepositoryId);
            var readable = Directory.Exists(payload.RepositoryRoot);
            if (!readable)
                return JsonSerializer.Serialize(new NasAccessTestResultDto(false, payload.RepositoryId, payload.RepositoryRoot, configured, false, false, "NAS yolu erişilebilir değil."), CommandResultJsonOptions);

            var probe = Path.Combine(payload.RepositoryRoot, ".yazmabackup-access-probe-" + Guid.NewGuid().ToString("N"));
            var writable = false;
            try
            {
                Directory.CreateDirectory(probe);
                writable = Directory.Exists(probe);
            }
            finally
            {
                if (Directory.Exists(probe)) Directory.Delete(probe, recursive: false);
            }

            return JsonSerializer.Serialize(new NasAccessTestResultDto(writable, payload.RepositoryId, payload.RepositoryRoot, configured, true, writable,
                writable ? "NAS okuma/yazma testi başarılı." : "NAS okunabiliyor ancak yazma testi başarısız."), CommandResultJsonOptions);
        }

        if (command.Type == "ProvisionRepositoryKey")
        {
            var payload = JsonSerializer.Deserialize<WrappedRepositoryKeyPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid wrapped repository key payload.");
            var clearKey = _keyExchange.UnwrapRepositoryKey(payload.WrappedKeyBase64);
            try
            {
                _repositoryKeys.Provision(payload.RepositoryId, payload.KeyId, clearKey, payload.MakeActive);
                var summary = _repositoryKeys.GetSummary(payload.RepositoryId);
                return JsonSerializer.Serialize(new { repositoryId = summary.RepositoryId, activeKeyId = summary.ActiveKeyId, keyCount = summary.KeyIds.Count }, CommandResultJsonOptions);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(clearKey);
            }
        }

        if (command.Type == "RemoveRepositoryKey")
        {
            var payload = JsonSerializer.Deserialize<RemoveRepositoryKeyPayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid remove repository key payload.");
            await _repositoryKeys.RemoveAfterRepositoryValidationAsync(
                payload.RepositoryRoot, payload.RepositoryId, payload.KeyId, cancellationToken).ConfigureAwait(false);
            var summary = _repositoryKeys.GetSummary(payload.RepositoryId);
            return JsonSerializer.Serialize(new { repositoryId = summary.RepositoryId, activeKeyId = summary.ActiveKeyId, keyCount = summary.KeyIds.Count }, CommandResultJsonOptions);
        }

        if (command.Type == "ClearProtectionLock")
        {
            var payload = JsonSerializer.Deserialize<ClearProtectionLockPayload>(command.PayloadJson) ?? new ClearProtectionLockPayload();
            var cleared = _protectionLock.Clear(payload.ExpectedIncidentId);
            AgentLog.Warning("protection-lock-cleared", "Protection lock was cleared by an authorized remote command.", new { payload.ExpectedIncidentId, cleared });
            return JsonSerializer.Serialize(new { cleared, protection = _protectionLock.Telemetry() }, CommandResultJsonOptions);
        }

        if (command.Type == "StageAgentUpdate")
        {
            var payload = JsonSerializer.Deserialize<StageAgentUpdatePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid update payload.");
            var stager = new AgentUpdateStager(_config);
            return JsonSerializer.Serialize(await stager.StageAsync(payload, cancellationToken).ConfigureAwait(false), CommandResultJsonOptions);
        }

        if (command.Type == "ApplyStagedAgentUpdate")
        {
            var payload = JsonSerializer.Deserialize<ApplyStagedAgentUpdatePayload>(command.PayloadJson) ?? throw new InvalidDataException("Invalid apply-update payload.");
            var applier = new AgentUpdateApplier(_config);
            return JsonSerializer.Serialize(applier.Schedule(payload), CommandResultJsonOptions);
        }

        throw new NotSupportedException($"Unsupported command type: {command.Type}");
    }

    private static void ValidateBackupPayload(BackupPayload payload)
    {
        if (payload.ActiveBytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("ActiveBytesPerSecond is outside the supported range.");
        if (payload.IdleBytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new InvalidDataException("IdleBytesPerSecond is outside the supported range.");
        if (payload.UserIdleThresholdSeconds is < 30 or > 86400)
            throw new InvalidDataException("UserIdleThresholdSeconds is outside the supported range.");
        payload.Retention.Validate();
        (payload.Protection ?? new ProtectionPolicy()).Validate();
    }

    private static BrowseResultDto Browse(string requestedPath)
    {
        if (string.Equals(requestedPath, "::drives", StringComparison.OrdinalIgnoreCase))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(d =>
                {
                    long? length = null;
                    try { if (d.IsReady) length = d.TotalSize; } catch (IOException) { }
                    return new BrowseEntryDto(d.Name, d.Name, true, length, DateTimeOffset.MinValue);
                })
                .ToArray();
            return new BrowseResultDto("::drives", drives);
        }

        var full = Path.GetFullPath(requestedPath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var entries = new List<BrowseEntryDto>();
        foreach (var path in Directory.EnumerateFileSystemEntries(full).Take(5000))
        {
            try
            {
                var attr = File.GetAttributes(path);
                var isDirectory = (attr & FileAttributes.Directory) != 0;
                long? length = isDirectory ? null : new FileInfo(path).Length;
                var last = isDirectory ? new DirectoryInfo(path).LastWriteTimeUtc : new FileInfo(path).LastWriteTimeUtc;
                entries.Add(new BrowseEntryDto(Path.GetFileName(path), path, isDirectory, length, last));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new BrowseResultDto(full, entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    private static string NormalizeRestorePrefix(string? prefix)
    {
        var value = (prefix ?? string.Empty).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        if (value.Length == 0) return string.Empty;
        if (Path.IsPathRooted(value) || value.Split(Path.DirectorySeparatorChar).Any(x => x is ".." or "." || x.Length == 0))
            throw new InvalidDataException("Restore explorer prefix must be a safe relative path.");
        return value;
    }

    private static RestoreEntryDto[] BuildRestoreEntries(BackupManifest manifest, string prefix)
    {
        var separator = Path.DirectorySeparatorChar;
        var prefixWithSeparator = prefix.Length == 0 ? string.Empty : prefix + separator;
        var directories = new Dictionary<string, RestoreEntryDto>(StringComparer.OrdinalIgnoreCase);
        var files = new List<RestoreEntryDto>();
        foreach (var file in manifest.Files)
        {
            var relative = file.RelativePath.Replace(Path.AltDirectorySeparatorChar, separator).TrimStart(separator);
            if (prefixWithSeparator.Length > 0 && !relative.StartsWith(prefixWithSeparator, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = prefixWithSeparator.Length == 0 ? relative : relative[prefixWithSeparator.Length..];
            if (remainder.Length == 0) continue;
            var cut = remainder.IndexOf(separator);
            if (cut >= 0)
            {
                var name = remainder[..cut];
                var childPath = prefixWithSeparator + name;
                directories.TryAdd(childPath, new RestoreEntryDto(name, childPath, true, null, null));
            }
            else
            {
                files.Add(new RestoreEntryDto(remainder, relative, false, file.Length, file.LastWriteTimeUtc));
            }
        }
        return directories.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(files.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)).Take(5000).ToArray();
    }

    private async Task RenewLeaseLoopAsync(Guid commandId, Guid leaseId, LeaseState leaseState, CancellationTokenSource operationCancellation, CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.LeaseRenewSeconds), stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var (identity, token) = ActiveCredentials();
                using var request = Authorized(HttpMethod.Post, $"/api/v1/agent/commands/{commandId}/lease", identity.AgentId, token,
                    JsonContent.Create(new RenewCommandLeaseRequest(leaseId)));
                using var response = await _http.SendAsync(request, stopToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    leaseState.Lost = true;
                    operationCancellation.Cancel();
                    return;
                }
                response.EnsureSuccessStatusCode();
                var renewed = await response.Content.ReadFromJsonAsync<RenewCommandLeaseResponse>(cancellationToken: stopToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Lease renewal response is empty.");
                leaseState.ExpiresAtUtc = renewed.LeaseExpiresAtUtc;
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AgentLog.Warning("lease-renewal-error", ex.Message, new { commandId, expiresAtUtc = leaseState.ExpiresAtUtc });
                if (DateTimeOffset.UtcNow.AddSeconds(15) >= leaseState.ExpiresAtUtc)
                {
                    leaseState.Lost = true;
                    operationCancellation.Cancel();
                    return;
                }
            }
        }
    }

    private async Task PublishTransferTelemetryLoopAsync(Guid commandId, string repositoryId, TransferActivityTracker tracker, BackupProgressTracker progress, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PublishTransferTelemetryAsync(commandId, repositoryId, "active", tracker, progress, startedAtUtc, cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private async Task PublishTransferTelemetryAsync(Guid commandId, string repositoryId, string state, TransferActivityTracker tracker, BackupProgressTracker progress, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            var sample = tracker.Snapshot();
            var progressSample = progress.Snapshot();
            var (identity, token) = ActiveCredentials();
            using var request = Authorized(HttpMethod.Post, "/api/v1/agent/transfer-telemetry", identity.AgentId, token,
                JsonContent.Create(new AgentTransferTelemetryRequest(commandId, "backup", state, repositoryId, sample.BytesTransferred, sample.BytesPerSecond, startedAtUtc, DateTimeOffset.UtcNow,
                    progressSample.Stage, progressSample.LogicalBytesProcessed, progressSample.LogicalBytesTotal, progressSample.FilesProcessed, progressSample.FilesTotal)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                AgentLog.Warning("transfer-telemetry-rejected", "Control Plane rejected transfer telemetry.", new { commandId, statusCode = (int)response.StatusCode });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AgentLog.Warning("transfer-telemetry-error", ex.Message, new { commandId });
        }
    }

    private async Task PostResultAsync(Guid commandId, Guid leaseId, bool succeeded, string resultJson, string? error, CancellationToken cancellationToken)
    {
        var (identity, token) = ActiveCredentials();
        using var request = Authorized(HttpMethod.Post, $"/api/v1/agent/commands/{commandId}/result", identity.AgentId, token,
            JsonContent.Create(new CommandResultRequest(leaseId, succeeded, resultJson, error)));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private (AgentIdentity Identity, string Token) ActiveCredentials()
    {
        return (_identity ?? throw new InvalidOperationException("Agent identity is unavailable."),
            _accessToken ?? throw new InvalidOperationException("Agent access token is unavailable."));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, Guid agentId, string token, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-YazmaBackup-Agent-Id", agentId.ToString("D"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureRepositoryOutsideSource(string sourcePath, string repositoryRoot)
    {
        var source = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repository = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePrefix = source + Path.DirectorySeparatorChar;
        if (repository.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) || string.Equals(repository, source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup repository cannot be located inside the protected source path.");
    }

    private static string SanitizeError(Exception exception)
    {
        var text = exception.GetType().Name + ": " + exception.Message;
        text = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 4000 ? text : text[..4000];
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed class LeaseState(DateTimeOffset expiresAtUtc)
    {
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        public bool Lost { get; set; }
    }
}
