using Microsoft.AspNetCore.Builder;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class RestoreOperationEndpoints
{
    internal static AdminEndpointGroups MapRestoreOperationEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Operate.MapPost("/agents/{agentId:guid}/restore-points", async (Guid agentId, HttpContext http, EnqueueListRestorePointsRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) || !ValidPath(request.SourceRoot))
                return Results.BadRequest(new { error = "Repository root, repository id and source root are required." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.ListRestorePoints,
                new ListRestorePointsPayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/restore-entries", async (Guid agentId, HttpContext http, EnqueueListRestoreEntriesRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) || !AgentRequestSecurity.ValidIdentifier(request.BackupId, 160) ||
                (request.Prefix is not null && request.Prefix.Length > 32767))
                return Results.BadRequest(new { error = "Invalid restore explorer request." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.ListRestoreEntries,
                new ListRestoreEntriesPayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.Prefix), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/restore", async (Guid agentId, HttpContext http, EnqueueRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) || !ValidPath(request.DestinationRoot) || !AgentRequestSecurity.ValidIdentifier(request.BackupId, 160))
                return Results.BadRequest(new { error = "Invalid restore request." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.RestoreBackup,
                new RestorePayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.DestinationRoot, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/restore-point-in-time", async (Guid agentId, HttpContext http, EnqueuePointInTimeRestoreRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) || !ValidPath(request.SourceRoot) || !ValidPath(request.DestinationRoot))
                return Results.BadRequest(new { error = "Invalid point-in-time restore request." });
            if (request.RestorePointUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                return Results.BadRequest(new { error = "Restore point cannot be in the future." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.RestorePointInTime,
                new RestorePointInTimePayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot, request.RestorePointUtc, request.DestinationRoot, request.OverwriteExisting), http, store, ct).ConfigureAwait(false);
        });

        groups.Operate.MapPost("/agents/{agentId:guid}/restore-sandbox", async (Guid agentId, HttpContext http, EnqueueRestoreSandboxRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) || !AgentRequestSecurity.ValidIdentifier(request.BackupId, 160) || !ValidPath(request.SandboxRoot))
                return Results.BadRequest(new { error = "Invalid restore sandbox request." });
            if (request.MaxFiles is < 1 or > 100000 || request.MaxBytes is < 1)
                return Results.BadRequest(new { error = "Sandbox limits are invalid." });
            return await AdminCommandQueue.EnqueueAsync(agentId, AgentCommandType.RestoreSandbox,
                new RestoreSandboxPayload(request.RepositoryRoot, request.RepositoryId, request.BackupId.Trim(), request.SandboxRoot, request.MaxFiles, request.MaxBytes), http, store, ct).ConfigureAwait(false);
        });

        return groups;
    }

    private static bool ValidPath(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
}
