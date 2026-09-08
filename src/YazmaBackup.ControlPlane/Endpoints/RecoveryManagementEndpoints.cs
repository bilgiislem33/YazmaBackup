using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class RecoveryManagementEndpoints
{
    internal static AdminEndpointGroups MapRecoveryManagementEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/recovery-fabric", async (RecoveryFabricService recoveryFabric, CancellationToken ct) =>
            Results.Ok(await recoveryFabric.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/repository-health", async (int? limit, IResilienceStore store, CancellationToken ct) =>
        {
            var records = await store.GetRepositoryHealthAsync(Math.Clamp(limit ?? 500, 1, 5000), ct).ConfigureAwait(false);
            return Results.Ok(records.Select(x => new RepositoryHealthDto(x.HealthId, x.PolicyId, x.AgentId, x.RepositoryId, x.RepositoryRoot, x.MeasuredAtUtc, x.TotalBytes, x.FreeBytes, x.RepositoryPhysicalBytes, x.RestorePointCount, x.LatestRestorePointUtc, x.NewBytesLast7Days, x.DailyGrowthBytes, x.EstimatedDaysToFull, x.Status, x.Reason)));
        });

        groups.Backup.MapPost("/recovery-plans", async (CreateRecoveryPlanRequest request, IResilienceStore store, CancellationToken ct) =>
        {
            try
            {
                var plan = await store.CreateRecoveryPlanAsync(request.Name, request.PolicyIds, request.MaxParallelAgents, request.RtoTargetMinutes, request.IntervalDays, request.Enabled, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/recovery-plans/{plan.PlanId}", new RecoveryPlanDto(plan.PlanId, plan.Name, plan.PolicyIds, plan.MaxParallelAgents, plan.RtoTargetMinutes, plan.IntervalDays, plan.Enabled, plan.CreatedAtUtc, plan.LastRunAtUtc, plan.NextRunAtUtc));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Read.MapGet("/recovery-plans", async (IResilienceStore store, CancellationToken ct) =>
        {
            var plans = await store.GetRecoveryPlansAsync(ct).ConfigureAwait(false);
            return Results.Ok(plans.Select(x => new RecoveryPlanDto(x.PlanId, x.Name, x.PolicyIds, x.MaxParallelAgents, x.RtoTargetMinutes, x.IntervalDays, x.Enabled, x.CreatedAtUtc, x.LastRunAtUtc, x.NextRunAtUtc)));
        });

        groups.Read.MapGet("/recovery-runs", async (int? limit, IResilienceStore store, CancellationToken ct) =>
        {
            var runs = await store.GetRecoveryRunsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
            return Results.Ok(runs.Select(x => new RecoveryRunDto(x.RunId, x.PlanId, x.StartedAtUtc, x.CompletedAtUtc, x.Status, x.RtoTargetMinutes, x.VerifiedBytes, x.LongestRestoreMilliseconds,
                x.Targets.Select(t => new RecoveryRunTargetDto(t.TargetId, t.PolicyId, t.AgentId, t.CommandId, t.Status, t.Succeeded, t.VerifiedBytes, t.DurationMilliseconds, t.Error)).ToArray())));
        });

        groups.Backup.MapPost("/recovery-runbooks", async (CreateRecoveryRunbookRequest request, IProductionFabricStore store, CancellationToken ct) =>
        {
            try
            {
                var runbook = await store.CreateRecoveryRunbookAsync(request.Name, request.RecoveryPlanIds, request.RtoBudgetMinutes, request.IntervalDays, request.Enabled, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/recovery-runbooks/{runbook.RunbookId}", ToRecoveryRunbookDto(runbook));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Read.MapGet("/recovery-runbooks", async (IProductionFabricStore store, CancellationToken ct) =>
        {
            var items = await store.GetRecoveryRunbooksAsync(ct).ConfigureAwait(false);
            return Results.Ok(items.Select(ToRecoveryRunbookDto));
        });

        groups.Read.MapGet("/recovery-runbook-runs", async (int? limit, IProductionFabricStore store, CancellationToken ct) =>
        {
            var items = await store.GetRecoveryRunbookRunsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
            return Results.Ok(items.Select(ToRecoveryRunbookRunDto));
        });

        groups.Read.MapGet("/recovery-runbook-runs/{runId:guid}/evidence", async (Guid runId, RecoveryEvidenceService evidence, CancellationToken ct) =>
        {
            var envelope = await evidence.BuildAsync(runId, ct).ConfigureAwait(false);
            return envelope is null ? Results.NotFound() : Results.Ok(envelope);
        });

        return groups;
    }
}
