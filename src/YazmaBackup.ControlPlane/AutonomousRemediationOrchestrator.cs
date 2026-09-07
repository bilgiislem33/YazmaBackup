using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record CreateAutonomousRemediationRequest(
    Guid AgentId,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    string ActionId,
    string? WorkingRoot = null);

public sealed record CreateCanaryRolloutRequest(
    string Name,
    string TargetVersion,
    string PackageUri,
    string Sha256,
    string SignatureBase64,
    IReadOnlyList<Guid>? TargetAgentIds = null,
    int CanaryPercent = 10,
    int MaxParallel = 5,
    int ObservationMinutes = 3,
    int RequiredScore = 95,
    double MinBackupSuccessPct = 98,
    double MinAgentOnlinePct = 95);

public sealed record AutonomousRemediationRun(
    Guid RunId,
    Guid AgentId,
    string SourcePath,
    string RepositoryRoot,
    string RepositoryId,
    string ActionId,
    string? WorkingRoot,
    string State,
    Guid? DiagnoseCommandId,
    Guid? ApplyCommandId,
    Guid? VerifyCommandId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    string? ApprovedBy,
    string? Error);

public sealed record CanaryRolloutTarget(
    Guid AgentId,
    string MachineName,
    string? InitialVersion,
    bool Canary,
    string State,
    Guid? StageCommandId,
    Guid? ApplyCommandId,
    DateTimeOffset? AppliedAtUtc,
    string? Error);

public sealed record CanaryRolloutRun(
    Guid RolloutId,
    string Name,
    string TargetVersion,
    string PackageUri,
    string Sha256,
    string SignatureBase64,
    int CanaryPercent,
    int MaxParallel,
    int ObservationMinutes,
    int RequiredScore,
    double MinBackupSuccessPct,
    double MinAgentOnlinePct,
    string State,
    string? HeldFromState,
    string? HoldReason,
    IReadOnlyList<Guid> CanaryAgentIds,
    IReadOnlyList<Guid> ActiveWaveAgentIds,
    int WaveNumber,
    DateTimeOffset? ObservationStartedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CanaryRolloutTarget> Targets,
    string RollbackMode = "local-installer-health-rollback");

internal sealed record AutonomousOrchestrationState(
    IReadOnlyList<AutonomousRemediationRun> Remediations,
    IReadOnlyList<CanaryRolloutRun> Rollouts);

public sealed class AutonomousOrchestrationStore : IDisposable
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AutonomousOrchestrationStore(string stateRoot, IDataProtectionProvider provider)
    {
        _path = Path.Combine(stateRoot, "autonomous-orchestration.protected");
        _protector = provider.CreateProtector("YazmaBackup.AutonomousOrchestration.v1");
    }

    public async Task<IReadOnlyList<AutonomousRemediationRun>> GetRemediationsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadUnlockedAsync(ct).ConfigureAwait(false)).Remediations.OrderByDescending(x => x.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CanaryRolloutRun>> GetRolloutsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadUnlockedAsync(ct).ConfigureAwait(false)).Rollouts.OrderByDescending(x => x.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<CanaryRolloutRun?> GetRolloutAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadUnlockedAsync(ct).ConfigureAwait(false)).Rollouts.FirstOrDefault(x => x.RolloutId == id); }
        finally { _gate.Release(); }
    }

    public async Task<AutonomousRemediationRun> CreateRemediationAsync(CreateAutonomousRemediationRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AutonomousRemediationRun(
            Guid.NewGuid(), request.AgentId, request.SourcePath.Trim(), request.RepositoryRoot.Trim(), request.RepositoryId.Trim(),
            request.ActionId.Trim(), request.WorkingRoot?.Trim(), "queued-diagnosis", null, null, null, now, now, null, null, null);
        await MutateAsync(state => state with { Remediations = state.Remediations.Append(run).TakeLast(2000).ToArray() }, ct).ConfigureAwait(false);
        return run;
    }

    public async Task<CanaryRolloutRun> CreateRolloutAsync(CreateCanaryRolloutRequest request, IReadOnlyList<AgentRecord> targets, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var canaryCount = Math.Clamp((int)Math.Ceiling(targets.Count * request.CanaryPercent / 100d), 1, targets.Count);
        var ordered = targets.OrderBy(x => x.MachineName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.AgentId).ToArray();
        var canaries = ordered.Take(canaryCount).Select(x => x.AgentId).ToArray();
        var rows = ordered.Select(a => new CanaryRolloutTarget(
            a.AgentId, a.MachineName, a.AgentVersion, canaries.Contains(a.AgentId), "pending", null, null, null, null)).ToArray();
        var run = new CanaryRolloutRun(
            Guid.NewGuid(), request.Name.Trim(), request.TargetVersion.Trim(), request.PackageUri.Trim(), request.Sha256.ToLowerInvariant(),
            request.SignatureBase64.Trim(), request.CanaryPercent, request.MaxParallel, request.ObservationMinutes, request.RequiredScore,
            request.MinBackupSuccessPct, request.MinAgentOnlinePct, "draft", null, null, canaries, Array.Empty<Guid>(), 0, null,
            now, now, rows);
        await MutateAsync(state => state with { Rollouts = state.Rollouts.Append(run).TakeLast(500).ToArray() }, ct).ConfigureAwait(false);
        return run;
    }

    public Task<AutonomousRemediationRun?> UpdateRemediationAsync(Guid id, Func<AutonomousRemediationRun, AutonomousRemediationRun> update, CancellationToken ct) =>
        MutateAndReturnAsync(id, update, ct);

    public Task<CanaryRolloutRun?> UpdateRolloutAsync(Guid id, Func<CanaryRolloutRun, CanaryRolloutRun> update, CancellationToken ct) =>
        MutateRolloutAndReturnAsync(id, update, ct);

    private async Task<AutonomousRemediationRun?> MutateAndReturnAsync(Guid id, Func<AutonomousRemediationRun, AutonomousRemediationRun> update, CancellationToken ct)
    {
        AutonomousRemediationRun? result = null;
        await MutateAsync(state =>
        {
            var rows = state.Remediations.Select(x =>
            {
                if (x.RunId != id) return x;
                result = update(x) with { UpdatedAtUtc = DateTimeOffset.UtcNow };
                return result;
            }).ToArray();
            return state with { Remediations = rows };
        }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<CanaryRolloutRun?> MutateRolloutAndReturnAsync(Guid id, Func<CanaryRolloutRun, CanaryRolloutRun> update, CancellationToken ct)
    {
        CanaryRolloutRun? result = null;
        await MutateAsync(state =>
        {
            var rows = state.Rollouts.Select(x =>
            {
                if (x.RolloutId != id) return x;
                result = update(x) with { UpdatedAtUtc = DateTimeOffset.UtcNow };
                return result;
            }).ToArray();
            return state with { Rollouts = rows };
        }, ct).ConfigureAwait(false);
        return result;
    }

    private async Task MutateAsync(Func<AutonomousOrchestrationState, AutonomousOrchestrationState> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            await WriteUnlockedAsync(mutate(current), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<AutonomousOrchestrationState> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new AutonomousOrchestrationState(Array.Empty<AutonomousRemediationRun>(), Array.Empty<CanaryRolloutRun>());
        var protectedText = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        var json = _protector.Unprotect(protectedText);
        return JsonSerializer.Deserialize<AutonomousOrchestrationState>(json, JsonOptions)
            ?? new AutonomousOrchestrationState(Array.Empty<AutonomousRemediationRun>(), Array.Empty<CanaryRolloutRun>());
    }

    private async Task WriteUnlockedAsync(AutonomousOrchestrationState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        var protectedText = _protector.Protect(JsonSerializer.Serialize(state, JsonOptions));
        await File.WriteAllTextAsync(temp, protectedText, ct).ConfigureAwait(false);
        File.Move(temp, _path, true);
    }

    public void Dispose() => _gate.Dispose();
}

public sealed partial class AutonomousRemediationOrchestratorService(
    AutonomousOrchestrationStore orchestration,
    IControlPlaneStore store,
    ILogger<AutonomousRemediationOrchestratorService> logger) : BackgroundService
{
    private readonly string _nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var lease = await store.TryAcquireOrRenewClusterLeaseAsync("autonomous-remediation-orchestrator", _nodeId, LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                if (lease is not null && string.Equals(lease.OwnerId, _nodeId, StringComparison.Ordinal))
                {
                    await ProcessRemediationsAsync(stoppingToken).ConfigureAwait(false);
                    await ProcessRolloutsAsync(now, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogIterationFailed(logger, ex, _nodeId); }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessRemediationsAsync(CancellationToken ct)
    {
        var runs = await orchestration.GetRemediationsAsync(ct).ConfigureAwait(false);
        foreach (var run in runs.Where(x => x.State is "queued-diagnosis" or "diagnosing" or "approved" or "applying" or "verifying"))
        {
            if (run.State == "queued-diagnosis")
            {
                var cmd = await store.EnqueueAsync(run.AgentId, AgentCommandType.SelfHealingDiagnose,
                    JsonSerializer.Serialize(new SelfHealingDiagnosePayload(run.SourcePath, run.RepositoryRoot, run.RepositoryId), WebJson),
                    $"autonomous-remediation:{run.RunId:N}:diagnose", ct).ConfigureAwait(false);
                await orchestration.UpdateRemediationAsync(run.RunId, x => x with { State = "diagnosing", DiagnoseCommandId = cmd.CommandId }, ct).ConfigureAwait(false);
                continue;
            }

            if (run.State == "diagnosing" && run.DiagnoseCommandId is Guid diagnoseId)
            {
                var cmd = await store.GetCommandAsync(diagnoseId, ct).ConfigureAwait(false);
                if (cmd?.CompletedAtUtc is null) continue;
                if (!cmd.Succeeded)
                {
                    await orchestration.UpdateRemediationAsync(run.RunId, x => x with { State = "failed", Error = cmd.Error ?? "Self-healing diagnosis failed." }, ct).ConfigureAwait(false);
                    continue;
                }
                await orchestration.UpdateRemediationAsync(run.RunId, x => x with
                {
                    State = string.Equals(x.ActionId, "diagnose-only", StringComparison.Ordinal) ? "completed" : "awaiting-approval",
                    Error = null
                }, ct).ConfigureAwait(false);
                continue;
            }

            if (run.State == "approved")
            {
                var cmd = await store.EnqueueAsync(run.AgentId, AgentCommandType.SelfHealingApply,
                    JsonSerializer.Serialize(new SelfHealingApplyPayload(run.RepositoryId, run.ActionId, run.WorkingRoot), WebJson),
                    $"autonomous-remediation:{run.RunId:N}:apply", ct).ConfigureAwait(false);
                await orchestration.UpdateRemediationAsync(run.RunId, x => x with { State = "applying", ApplyCommandId = cmd.CommandId }, ct).ConfigureAwait(false);
                continue;
            }

            if (run.State == "applying" && run.ApplyCommandId is Guid applyId)
            {
                var cmd = await store.GetCommandAsync(applyId, ct).ConfigureAwait(false);
                if (cmd?.CompletedAtUtc is null) continue;
                if (!cmd.Succeeded)
                {
                    await orchestration.UpdateRemediationAsync(run.RunId, x => x with { State = "failed", Error = cmd.Error ?? "Self-healing apply failed." }, ct).ConfigureAwait(false);
                    continue;
                }
                var verify = await store.EnqueueAsync(run.AgentId, AgentCommandType.SelfHealingDiagnose,
                    JsonSerializer.Serialize(new SelfHealingDiagnosePayload(run.SourcePath, run.RepositoryRoot, run.RepositoryId), WebJson),
                    $"autonomous-remediation:{run.RunId:N}:verify", ct).ConfigureAwait(false);
                await orchestration.UpdateRemediationAsync(run.RunId, x => x with { State = "verifying", VerifyCommandId = verify.CommandId }, ct).ConfigureAwait(false);
                continue;
            }

            if (run.State == "verifying" && run.VerifyCommandId is Guid verifyId)
            {
                var cmd = await store.GetCommandAsync(verifyId, ct).ConfigureAwait(false);
                if (cmd?.CompletedAtUtc is null) continue;
                await orchestration.UpdateRemediationAsync(run.RunId, x => x with
                {
                    State = cmd.Succeeded ? "completed" : "failed",
                    Error = cmd.Succeeded ? null : cmd.Error ?? "Post-remediation verification failed."
                }, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessRolloutsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rollouts = await orchestration.GetRolloutsAsync(ct).ConfigureAwait(false);
        foreach (var run in rollouts.Where(x => x.State is not ("draft" or "held" or "cancelled" or "completed" or "failed")))
        {
            var current = run;
            if (current.State == "preflight")
            {
                var health = await EvaluateHealthAsync(Array.Empty<Guid>(), current.ObservationStartedAtUtc, ct).ConfigureAwait(false);
                if (!HealthPasses(current, health))
                {
                    await HoldAsync(current, "preflight", $"Preflight health gate failed: score={health.Score}, backup={health.BackupSuccessPct:F1}%, online={health.AgentOnlinePct:F1}%.", ct).ConfigureAwait(false);
                    continue;
                }
                await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "canary-staging", HoldReason = null, HeldFromState = null }, ct).ConfigureAwait(false);
                continue;
            }

            if (current.State == "canary-staging")
            {
                current = await EnsureStageCommandsAsync(current, current.CanaryAgentIds, "canary", ct).ConfigureAwait(false);
                var result = await CommandsCompleteAsync(current, current.CanaryAgentIds, stage: true, ct).ConfigureAwait(false);
                if (result.Failed)
                {
                    await HoldAsync(current, "canary-staging", result.Error!, ct).ConfigureAwait(false);
                    continue;
                }
                if (result.Complete)
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "canary-applying" }, ct).ConfigureAwait(false);
                continue;
            }

            if (current.State == "canary-applying")
            {
                current = await EnsureApplyCommandsAsync(current, current.CanaryAgentIds, "canary", ct).ConfigureAwait(false);
                var result = await CommandsCompleteAsync(current, current.CanaryAgentIds, stage: false, ct).ConfigureAwait(false);
                if (result.Failed)
                {
                    await HoldAsync(current, "canary-applying", result.Error!, ct).ConfigureAwait(false);
                    continue;
                }
                if (result.Complete)
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "canary-observing", ObservationStartedAtUtc = now }, ct).ConfigureAwait(false);
                continue;
            }

            if (current.State == "canary-observing")
            {
                var targetReady = await TargetVersionsReadyAsync(current, current.CanaryAgentIds, ct).ConfigureAwait(false);
                if (!targetReady)
                {
                    if (current.ObservationStartedAtUtc is DateTimeOffset started && now - started > TimeSpan.FromMinutes(10))
                        await HoldAsync(current, "canary-observing", "Canary Agent heartbeat/version convergence timed out.", ct).ConfigureAwait(false);
                    continue;
                }
                var health = await EvaluateHealthAsync(current.CanaryAgentIds, current.ObservationStartedAtUtc, ct).ConfigureAwait(false);
                if (!HealthPasses(current, health))
                {
                    await HoldAsync(current, "canary-observing", $"Canary health gate failed: score={health.Score}, backup={health.BackupSuccessPct:F1}%, online={health.AgentOnlinePct:F1}%, waveFailures={health.WaveBackupFailures}.", ct).ConfigureAwait(false);
                    continue;
                }
                if (current.ObservationStartedAtUtc is DateTimeOffset observation && now - observation >= TimeSpan.FromMinutes(current.ObservationMinutes))
                {
                    var updatedTargets = current.Targets.Select(t => current.CanaryAgentIds.Contains(t.AgentId) ? t with { State = "completed" } : t).ToArray();
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "wave-staging", Targets = updatedTargets, ObservationStartedAtUtc = null }, ct).ConfigureAwait(false);
                }
                continue;
            }

            if (current.State == "wave-staging")
            {
                var remaining = current.ActiveWaveAgentIds.Count > 0
                    ? current.ActiveWaveAgentIds.ToArray()
                    : current.Targets.Where(t => !t.Canary && t.State == "pending").Take(current.MaxParallel).Select(t => t.AgentId).ToArray();
                if (remaining.Length == 0)
                {
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "completed", ActiveWaveAgentIds = Array.Empty<Guid>(), HoldReason = null }, ct).ConfigureAwait(false);
                    continue;
                }
                if (current.ActiveWaveAgentIds.Count == 0)
                    current = await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { ActiveWaveAgentIds = remaining, WaveNumber = x.WaveNumber + 1 }, ct).ConfigureAwait(false) ?? current;
                current = await EnsureStageCommandsAsync(current, remaining, $"wave-{current.WaveNumber}", ct).ConfigureAwait(false);
                var result = await CommandsCompleteAsync(current, remaining, stage: true, ct).ConfigureAwait(false);
                if (result.Failed)
                {
                    await HoldAsync(current, "wave-staging", result.Error!, ct).ConfigureAwait(false);
                    continue;
                }
                if (result.Complete)
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "wave-applying" }, ct).ConfigureAwait(false);
                continue;
            }

            if (current.State == "wave-applying")
            {
                var ids = current.ActiveWaveAgentIds;
                current = await EnsureApplyCommandsAsync(current, ids, $"wave-{current.WaveNumber}", ct).ConfigureAwait(false);
                var result = await CommandsCompleteAsync(current, ids, stage: false, ct).ConfigureAwait(false);
                if (result.Failed)
                {
                    await HoldAsync(current, "wave-applying", result.Error!, ct).ConfigureAwait(false);
                    continue;
                }
                if (result.Complete)
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "wave-observing", ObservationStartedAtUtc = now }, ct).ConfigureAwait(false);
                continue;
            }

            if (current.State == "wave-observing")
            {
                if (!await TargetVersionsReadyAsync(current, current.ActiveWaveAgentIds, ct).ConfigureAwait(false))
                {
                    if (current.ObservationStartedAtUtc is DateTimeOffset started && now - started > TimeSpan.FromMinutes(10))
                        await HoldAsync(current, "wave-observing", "Wave Agent heartbeat/version convergence timed out.", ct).ConfigureAwait(false);
                    continue;
                }
                var health = await EvaluateHealthAsync(current.ActiveWaveAgentIds, current.ObservationStartedAtUtc, ct).ConfigureAwait(false);
                if (!HealthPasses(current, health))
                {
                    await HoldAsync(current, "wave-observing", $"Wave health gate failed: score={health.Score}, backup={health.BackupSuccessPct:F1}%, online={health.AgentOnlinePct:F1}%, waveFailures={health.WaveBackupFailures}.", ct).ConfigureAwait(false);
                    continue;
                }
                if (current.ObservationStartedAtUtc is DateTimeOffset observation && now - observation >= TimeSpan.FromMinutes(current.ObservationMinutes))
                {
                    var updatedTargets = current.Targets.Select(t => current.ActiveWaveAgentIds.Contains(t.AgentId) ? t with { State = "completed" } : t).ToArray();
                    await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with { State = "wave-staging", Targets = updatedTargets, ActiveWaveAgentIds = Array.Empty<Guid>(), ObservationStartedAtUtc = null }, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<CanaryRolloutRun> EnsureStageCommandsAsync(CanaryRolloutRun run, IReadOnlyList<Guid> ids, string phase, CancellationToken ct)
    {
        var current = run;
        foreach (var id in ids)
        {
            var row = current.Targets.First(x => x.AgentId == id);
            if (row.StageCommandId is not null) continue;
            var cmd = await store.EnqueueAsync(id, AgentCommandType.StageAgentUpdate,
                JsonSerializer.Serialize(new StageAgentUpdatePayload(current.TargetVersion, current.PackageUri, current.Sha256, current.SignatureBase64), WebJson),
                $"canary-rollout:{current.RolloutId:N}:{phase}:stage:{id:N}:{current.TargetVersion}", ct).ConfigureAwait(false);
            current = await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with
            {
                Targets = x.Targets.Select(t => t.AgentId == id ? t with { State = "staging", StageCommandId = cmd.CommandId } : t).ToArray()
            }, ct).ConfigureAwait(false) ?? current;
        }
        return current;
    }

    private async Task<CanaryRolloutRun> EnsureApplyCommandsAsync(CanaryRolloutRun run, IReadOnlyList<Guid> ids, string phase, CancellationToken ct)
    {
        var current = run;
        foreach (var id in ids)
        {
            var row = current.Targets.First(x => x.AgentId == id);
            if (row.ApplyCommandId is not null) continue;
            var cmd = await store.EnqueueAsync(id, AgentCommandType.ApplyStagedAgentUpdate,
                JsonSerializer.Serialize(new ApplyStagedAgentUpdatePayload(current.TargetVersion), WebJson),
                $"canary-rollout:{current.RolloutId:N}:{phase}:apply:{id:N}:{current.TargetVersion}", ct).ConfigureAwait(false);
            current = await orchestration.UpdateRolloutAsync(current.RolloutId, x => x with
            {
                Targets = x.Targets.Select(t => t.AgentId == id ? t with { State = "applying", ApplyCommandId = cmd.CommandId, AppliedAtUtc = DateTimeOffset.UtcNow } : t).ToArray()
            }, ct).ConfigureAwait(false) ?? current;
        }
        return current;
    }

    private async Task<(bool Complete, bool Failed, string? Error)> CommandsCompleteAsync(CanaryRolloutRun run, IReadOnlyList<Guid> ids, bool stage, CancellationToken ct)
    {
        var complete = true;
        foreach (var id in ids)
        {
            var row = run.Targets.First(x => x.AgentId == id);
            var commandId = stage ? row.StageCommandId : row.ApplyCommandId;
            if (commandId is null) { complete = false; continue; }
            var cmd = await store.GetCommandAsync(commandId.Value, ct).ConfigureAwait(false);
            if (cmd?.CompletedAtUtc is null) { complete = false; continue; }
            if (!cmd.Succeeded) return (false, true, $"{row.MachineName}: {(stage ? "stage" : "apply")} failed · {cmd.Error}");
        }
        return (complete, false, null);
    }

    private async Task<bool> TargetVersionsReadyAsync(CanaryRolloutRun run, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-3);
        foreach (var id in ids)
        {
            var agent = agents.FirstOrDefault(x => x.AgentId == id);
            if (agent is null || agent.LastSeenUtc < cutoff || !string.Equals(agent.AgentVersion, run.TargetVersion, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private async Task<RolloutHealth> EvaluateHealthAsync(IReadOnlyList<Guid> waveIds, DateTimeOffset? since, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var commands = await store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
        var onlineCutoff = now.AddMinutes(-3);
        var completedBackups = commands.Where(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && c.CreatedAtUtc >= now.AddHours(-24)).ToArray();
        var failures = completedBackups.Count(c => !c.Succeeded);
        var backupPct = completedBackups.Length == 0 ? 100d : Math.Round(100d * (completedBackups.Length - failures) / completedBackups.Length, 1);
        var onlinePct = agents.Count == 0 ? 100d : Math.Round(100d * agents.Count(a => a.LastSeenUtc >= onlineCutoff) / agents.Count, 1);
        var critical = agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase));
        var waveFailures = waveIds.Count == 0 || since is null ? 0 : completedBackups.Count(c => waveIds.Contains(c.AgentId) && c.CreatedAtUtc >= since.Value && !c.Succeeded);
        var score = (int)Math.Round(Math.Clamp(backupPct * .55 + onlinePct * .30 + (critical == 0 ? 15 : 0), 0, 100));
        return new RolloutHealth(score, backupPct, onlinePct, critical, waveFailures);
    }

    private static bool HealthPasses(CanaryRolloutRun run, RolloutHealth health) =>
        health.Score >= run.RequiredScore &&
        health.BackupSuccessPct >= run.MinBackupSuccessPct &&
        health.AgentOnlinePct >= run.MinAgentOnlinePct &&
        health.CriticalIncidents == 0 &&
        health.WaveBackupFailures == 0;

    private async Task HoldAsync(CanaryRolloutRun run, string fromState, string reason, CancellationToken ct)
    {
        await orchestration.UpdateRolloutAsync(run.RolloutId, x => x with { State = "held", HeldFromState = fromState, HoldReason = reason }, ct).ConfigureAwait(false);
        LogRolloutHeld(logger, run.RolloutId, reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Canary rollout {RolloutId} held: {Reason}")]
    private static partial void LogRolloutHeld(ILogger logger, Guid rolloutId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Autonomous remediation orchestrator iteration failed on node {NodeId}.")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception, string nodeId);

    private sealed record RolloutHealth(int Score, double BackupSuccessPct, double AgentOnlinePct, int CriticalIncidents, int WaveBackupFailures);
}
