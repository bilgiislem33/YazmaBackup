using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AgentMaintenanceEndpoints
{
    internal static AdminEndpointGroups MapAgentMaintenanceEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Operate.MapPut("/agents/{agentId:guid}/assigned-user", async (Guid agentId, SetAgentAssignedUserRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            try
            {
                var updated = await store.SetAgentAssignedUserAsync(agentId, request.AssignedUser, ct).ConfigureAwait(false);
                if (updated is null) return Results.NotFound();
                return Results.Ok(new { updated.AgentId, updated.AssignedUser });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/pilot-readiness-probe", async (Guid agentId, HttpContext http, PilotReadinessProbePayload request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
            if (request.MinimumFreeBytes is < 0 or > 1024L * 1024 * 1024 * 1024 * 1024)
                return Results.BadRequest(new { error = "MinimumFreeBytes is outside the supported range." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.PilotReadinessProbe, request, http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/deep-validation", async (Guid agentId, HttpContext http, DeepValidationProbePayload request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.DeepValidationProbe, request, http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/granular-restore", async (Guid agentId, HttpContext http, EnqueueGranularRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128) || !ValidIdentifier(request.BackupId, 160) || !ValidPathInput(request.DestinationRoot))
                return Results.BadRequest(new { error = "Invalid granular restore request." });
            if (request.IncludePaths is null || request.IncludePaths.Count is < 1 or > 5000 || request.IncludePaths.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32767))
                return Results.BadRequest(new { error = "IncludePaths must contain 1..5000 valid relative paths." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.GranularRestore,
                new GranularRestorePayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.DestinationRoot, request.IncludePaths, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/self-healing/diagnose", async (Guid agentId, HttpContext http, SelfHealingDiagnoseRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Invalid self-healing diagnosis request." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.SelfHealingDiagnose,
                new SelfHealingDiagnosePayload(request.SourcePath, request.RepositoryRoot, request.RepositoryId), http, store, ct).ConfigureAwait(false);
        });

        groups.Security.MapPost("/agents/{agentId:guid}/self-healing/apply", async (Guid agentId, HttpContext http, SelfHealingApplyRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidIdentifier(request.RepositoryId, 128) || request.ActionId is not ("reset-repository-circuit" or "cleanup-stale-restore-temp"))
                return Results.BadRequest(new { error = "Self-healing action is not allowlisted." });
            if (request.ActionId == "cleanup-stale-restore-temp" && !ValidPathInput(request.WorkingRoot ?? string.Empty))
                return Results.BadRequest(new { error = "WorkingRoot is required for temp cleanup." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.SelfHealingApply,
                new SelfHealingApplyPayload(request.RepositoryId, request.ActionId, request.WorkingRoot), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/browse", async (Guid agentId, HttpContext http, EnqueueBrowseRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPathInput(request.Path)) return Results.BadRequest(new { error = "Path is required and must be <= 32767 characters." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.BrowsePath, new BrowsePayload(request.Path), http, store, ct).ConfigureAwait(false);
        });

        groups.Security.MapPost("/agents/{agentId:guid}/protection/clear", async (Guid agentId, HttpContext http, ClearProtectionLockRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.ClearProtectionLock,
                new ClearProtectionLockPayload(request.ExpectedIncidentId), http, store, ct).ConfigureAwait(false);
        });

        groups.Security.MapPost("/agents/{agentId:guid}/stage-update", async (Guid agentId, HttpContext http, EnqueueStageUpdateRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidIdentifier(request.Version, 64) || !ValidSha256(request.Sha256) || string.IsNullOrWhiteSpace(request.SignatureBase64) || request.SignatureBase64.Length > 2048)
                return Results.BadRequest(new { error = "Invalid update metadata." });
            if (!Uri.TryCreate(request.PackageUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
                return Results.BadRequest(new { error = "Update package URI must use https:// or file://." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.StageAgentUpdate,
                new StageAgentUpdatePayload(request.Version.Trim(), request.PackageUri.Trim(), request.Sha256.ToLowerInvariant(), request.SignatureBase64.Trim()), http, store, ct).ConfigureAwait(false);
        });

        groups.Security.MapPost("/agents/{agentId:guid}/apply-staged-update", async (Guid agentId, HttpContext http, ApplyStagedAgentUpdatePayload request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidIdentifier(request.Version, 64) || !Version.TryParse(request.Version, out _))
                return Results.BadRequest(new { error = "Invalid Agent update version." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.ApplyStagedAgentUpdate,
                new ApplyStagedAgentUpdatePayload(request.Version.Trim()), http, store, ct).ConfigureAwait(false);
        });

        return groups;
    }
}
