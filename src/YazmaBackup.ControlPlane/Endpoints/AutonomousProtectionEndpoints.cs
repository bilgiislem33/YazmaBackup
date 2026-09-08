using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AutonomousProtectionEndpoints
{
    internal static AdminEndpointGroups MapAutonomousProtectionEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/fleet-autopilot", async (FleetAutopilotService autopilot, CancellationToken ct) =>
            Results.Ok(await autopilot.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/predictive-protection", async (PredictiveProtectionService predictive, CancellationToken ct) =>
            Results.Ok(await predictive.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/closed-loop-protection", async (ClosedLoopProtectionService closedLoop, CancellationToken ct) =>
            Results.Ok(await closedLoop.BuildAsync(ct).ConfigureAwait(false)));

        groups.Backup.MapPost("/closed-loop-protection/{caseId}/diagnose", async (string caseId, ClosedLoopProtectionService closedLoop, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(caseId) || caseId.Length > 160)
                return Results.BadRequest(new { error = "Case id is invalid." });
            var run = await closedLoop.StartSafeDiagnosisAsync(caseId, ct).ConfigureAwait(false);
            return run is null
                ? Results.Conflict(new { error = "This case is not eligible for safe automatic diagnosis." })
                : Results.Accepted($"/api/v1/admin/autonomous/remediations/{run.RunId}", run);
        });

        groups.Read.MapGet("/autonomous-reliability", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
            var commands = await store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
            var onlineCutoff = now.AddMinutes(-3);

            var incidents = new List<object>();
            foreach (var agent in agents.Where(a => a.LastSeenUtc < onlineCutoff))
            {
                incidents.Add(new
                {
                    id = $"agent-offline:{agent.AgentId:N}",
                    severity = "warning",
                    category = "agent-offline",
                    agentId = agent.AgentId,
                    machineName = agent.MachineName,
                    title = "Agent çevrimdışı",
                    detail = $"Son bağlantı: {agent.LastSeenUtc:O}",
                    safeAutoAction = (string?)null,
                    requiresApproval = false
                });
            }

            foreach (var policy in policies.Where(p => p.Enabled && p.NextRunAtUtc < now.AddMinutes(-Math.Max(10, p.IntervalMinutes))))
            {
                incidents.Add(new
                {
                    id = $"policy-overdue:{policy.PolicyId:N}",
                    severity = "warning",
                    category = "policy-overdue",
                    agentId = policy.AgentId,
                    machineName = agents.FirstOrDefault(a => a.AgentId == policy.AgentId)?.MachineName,
                    title = "Yedekleme politikası gecikmiş",
                    detail = $"{policy.Name} · sıradaki çalışma {policy.NextRunAtUtc:O}",
                    sourcePath = policy.SourcePath,
                    repositoryRoot = policy.RepositoryRoot,
                    repositoryId = policy.RepositoryId,
                    safeAutoAction = "diagnose",
                    requiresApproval = false
                });
            }

            foreach (var command in commands.Where(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && !c.Succeeded && c.CreatedAtUtc >= now.AddHours(-24)))
            {
                BackupPayload? request = null;
                if (!string.IsNullOrWhiteSpace(command.PayloadJson))
                {
                    try { request = JsonSerializer.Deserialize<BackupPayload>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                    catch (JsonException) { }
                }
                var category = ClassifyBackupError(command.Error);
                var safeAction = category is "repository-circuit" or "repository" ? "reset-repository-circuit" : "diagnose";
                incidents.Add(new
                {
                    id = $"backup-failed:{command.CommandId:N}",
                    severity = category is "encryption-key" or "nas-authentication" ? "critical" : "warning",
                    category,
                    agentId = command.AgentId,
                    machineName = agents.FirstOrDefault(a => a.AgentId == command.AgentId)?.MachineName,
                    title = "Backup başarısız",
                    detail = command.Error,
                    sourcePath = request?.Path,
                    repositoryRoot = request?.RepositoryRoot,
                    repositoryId = request?.RepositoryId,
                    safeAutoAction = safeAction,
                    requiresApproval = safeAction == "reset-repository-circuit"
                });
            }

            foreach (var agent in agents.Where(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase)))
            {
                incidents.Add(new
                {
                    id = $"protection-locked:{agent.AgentId:N}",
                    severity = "critical",
                    category = "protection-locked",
                    agentId = agent.AgentId,
                    machineName = agent.MachineName,
                    title = "Protection lock aktif",
                    detail = "Endpoint koruması kilitli durumda.",
                    safeAutoAction = (string?)null,
                    requiresApproval = true
                });
            }

            var critical = incidents.Count(x => string.Equals((string?)x.GetType().GetProperty("severity")?.GetValue(x), "critical", StringComparison.OrdinalIgnoreCase));
            var warning = incidents.Count - critical;
            var recentBackupAttempts = commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && c.CreatedAtUtc >= now.AddHours(-24));
            var recentBackupFailures = commands.Count(c => c.Type == AgentCommandType.BackupPath && c.CompletedAtUtc is not null && c.CreatedAtUtc >= now.AddHours(-24) && !c.Succeeded);
            var backupSuccessPct = recentBackupAttempts == 0 ? 100d : Math.Round(100d * (recentBackupAttempts - recentBackupFailures) / recentBackupAttempts, 1);
            var onlinePct = agents.Count == 0 ? 100d : Math.Round(100d * agents.Count(a => a.LastSeenUtc >= onlineCutoff) / agents.Count, 1);
            var score = (int)Math.Round(Math.Clamp(backupSuccessPct * .55 + onlinePct * .30 + (critical == 0 ? 15 : 0), 0, 100));
            var canaryReady = score >= 95 && critical == 0 && recentBackupFailures == 0;

            return Results.Ok(new
            {
                generatedAtUtc = now,
                score,
                canaryReady,
                rolloutGuard = canaryReady ? "pass" : "hold",
                criticalIncidents = critical,
                warningIncidents = warning,
                backupSuccessPct,
                agentOnlinePct = onlinePct,
                incidents = incidents.Take(200).ToArray(),
                policy = new
                {
                    autonomousDiagnosis = true,
                    silentMutation = false,
                    allowlistedMutations = AutonomousMutationAllowlist.Values,
                    approvalRequiredForMutation = true
                }
            });
        });

        groups.Read.MapGet("/autonomous/remediations", async (AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
            Results.Ok(await orchestration.GetRemediationsAsync(ct).ConfigureAwait(false)));

        groups.Security.MapPost("/autonomous/remediations", async (CreateAutonomousRemediationRequest request, AutonomousOrchestrationStore orchestration, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (request.AgentId == Guid.Empty || !ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Remediation target/source/repository input is invalid." });
            if (request.ActionId is not ("diagnose-only" or "reset-repository-circuit" or "cleanup-stale-restore-temp"))
                return Results.BadRequest(new { error = "Remediation action is not allowlisted." });
            if (request.ActionId == "cleanup-stale-restore-temp" && !ValidPathInput(request.WorkingRoot ?? string.Empty))
                return Results.BadRequest(new { error = "WorkingRoot is required for temp cleanup." });
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            if (!agents.Any(x => x.AgentId == request.AgentId)) return Results.NotFound(new { error = "Agent not found." });
            var run = await orchestration.CreateRemediationAsync(request, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/admin/autonomous/remediations/{run.RunId}", run);
        });

        groups.Security.MapPost("/autonomous/remediations/{runId:guid}/approve", async (Guid runId, HttpContext http, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
            var run = await orchestration.UpdateRemediationAsync(runId, x =>
                x.State == "awaiting-approval"
                    ? x with { State = "approved", ApprovedAtUtc = DateTimeOffset.UtcNow, ApprovedBy = actor, Error = null }
                    : x, ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : run.State == "approved" ? Results.Ok(run) : Results.Conflict(new { error = "Remediation is not awaiting approval.", run.State });
        });

        groups.Security.MapPost("/autonomous/remediations/{runId:guid}/cancel", async (Guid runId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var run = await orchestration.UpdateRemediationAsync(runId, x =>
                x.State is "completed" or "failed" ? x : x with { State = "cancelled" }, ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        groups.Read.MapGet("/autonomous/rollouts", async (AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
            Results.Ok(await orchestration.GetRolloutsAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/autonomous/rollouts/{rolloutId:guid}", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var rollout = await orchestration.GetRolloutAsync(rolloutId, ct).ConfigureAwait(false);
            return rollout is null ? Results.NotFound() : Results.Ok(rollout);
        });

        groups.Security.MapPost("/autonomous/rollouts", async (CreateCanaryRolloutRequest request, AutonomousOrchestrationStore orchestration, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidText(request.Name, 128) || !ValidIdentifier(request.TargetVersion, 64) || !Version.TryParse(request.TargetVersion, out var targetVersion) || targetVersion is null)
                return Results.BadRequest(new { error = "Rollout name or target version is invalid." });
            if (!ValidSha256(request.Sha256) || string.IsNullOrWhiteSpace(request.SignatureBase64) || request.SignatureBase64.Length > 2048)
                return Results.BadRequest(new { error = "Rollout package hash/signature is invalid." });
            if (!Uri.TryCreate(request.PackageUri, UriKind.Absolute, out var packageUri) || (packageUri.Scheme != Uri.UriSchemeHttps && packageUri.Scheme != Uri.UriSchemeFile))
                return Results.BadRequest(new { error = "Rollout package URI must use https:// or file://." });
            if (request.CanaryPercent is < 1 or > 50 || request.MaxParallel is < 1 or > 25 || request.ObservationMinutes is < 1 or > 60 ||
                request.RequiredScore is < 90 or > 100 || request.MinBackupSuccessPct is < 90 or > 100 || request.MinAgentOnlinePct is < 80 or > 100)
                return Results.BadRequest(new { error = "Rollout safety thresholds are outside supported ranges." });

            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var requestedIds = request.TargetAgentIds?.Distinct().ToHashSet() ?? new HashSet<Guid>();
            var candidates = agents
                .Where(a => requestedIds.Count == 0 || requestedIds.Contains(a.AgentId))
                .Where(a => Version.TryParse(a.AgentVersion, out var current) && current is not null && current < targetVersion)
                .ToArray();
            if (requestedIds.Count > 0 && candidates.Length != requestedIds.Count)
                return Results.BadRequest(new { error = "One or more requested Agents are missing, have unknown versions, or are already at/newer than target." });
            if (candidates.Length == 0) return Results.BadRequest(new { error = "No eligible Agents for this rollout." });

            var rollout = await orchestration.CreateRolloutAsync(request, candidates, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/admin/autonomous/rollouts/{rollout.RolloutId}", rollout);
        });

        groups.Security.MapPost("/autonomous/rollouts/{rolloutId:guid}/start", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
                x.State == "draft" ? x with { State = "preflight", HoldReason = null, HeldFromState = null } : x, ct).ConfigureAwait(false);
            return rollout is null ? Results.NotFound() : rollout.State == "preflight" ? Results.Ok(rollout) : Results.Conflict(new { error = "Rollout must be draft before start.", rollout.State });
        });

        groups.Security.MapPost("/autonomous/rollouts/{rolloutId:guid}/hold", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
                x.State is "completed" or "cancelled" or "failed" or "held" ? x : x with { HeldFromState = x.State, State = "held", HoldReason = "Operator hold." }, ct).ConfigureAwait(false);
            return rollout is null ? Results.NotFound() : Results.Ok(rollout);
        });

        groups.Security.MapPost("/autonomous/rollouts/{rolloutId:guid}/resume", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
                x.State == "held" ? x with { State = x.HeldFromState ?? "preflight", HeldFromState = null, HoldReason = null } : x, ct).ConfigureAwait(false);
            return rollout is null ? Results.NotFound() : Results.Ok(rollout);
        });

        groups.Security.MapPost("/autonomous/rollouts/{rolloutId:guid}/cancel", async (Guid rolloutId, AutonomousOrchestrationStore orchestration, CancellationToken ct) =>
        {
            var rollout = await orchestration.UpdateRolloutAsync(rolloutId, x =>
                x.State == "completed" ? x : x with { State = "cancelled", HoldReason = "Operator cancelled." }, ct).ConfigureAwait(false);
            return rollout is null ? Results.NotFound() : Results.Ok(rollout);
        });

        return groups;
    }
}
