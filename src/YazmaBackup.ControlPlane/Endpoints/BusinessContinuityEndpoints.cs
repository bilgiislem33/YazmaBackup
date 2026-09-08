using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class BusinessContinuityEndpoints
{
    internal static AdminEndpointGroups MapBusinessContinuityEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/business-continuity", async (BusinessContinuityService continuity, CancellationToken ct) =>
            Results.Ok(await continuity.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/business-service-graph", async (BusinessServiceGraphService graph, CancellationToken ct) =>
            Results.Ok(await graph.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/dr-sessions", async (DisasterRecoveryExecutionService dr, CancellationToken ct) =>
            Results.Ok(await dr.GetAsync(ct).ConfigureAwait(false)));

        groups.Backup.MapPost("/dr-sessions", async (CreateDrSessionRequest request, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
        {
            try { return Results.Ok(await dr.StartAsync(request.Name, ct).ConfigureAwait(false)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/dr-sessions/{sessionId:guid}/steps/{order:int}/approve", async (Guid sessionId, int order, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
        {
            try { return Results.Ok(await dr.ApproveAsync(sessionId, order, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/dr-sessions/{sessionId:guid}/steps/{order:int}/verify", async (Guid sessionId, int order, VerifyDrStepRequest request, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
        {
            try { return Results.Ok(await dr.VerifyAsync(sessionId, order, request.VerificationNote, ct).ConfigureAwait(false)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/dr-sessions/{sessionId:guid}/cancel", async (Guid sessionId, DisasterRecoveryExecutionService dr, CancellationToken ct) =>
        {
            try { return Results.Ok(await dr.CancelAsync(sessionId, ct).ConfigureAwait(false)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/business-service-dependencies", async (UpsertBusinessServiceDependencyRequest request, IBusinessContinuityStore store, CancellationToken ct) =>
        {
            try
            {
                var record = await store.UpsertDependencyAsync(request.ServiceId, request.DependsOnServiceId, request.DependencyType, request.Required, ct).ConfigureAwait(false);
                return Results.Ok(record);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Backup.MapDelete("/business-service-dependencies/{dependencyId:guid}", async (Guid dependencyId, IBusinessContinuityStore store, CancellationToken ct) =>
            await store.DeleteDependencyAsync(dependencyId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

        return groups;
    }
}
