using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AdminCommandQueue
{
    internal static async Task<IResult> EnqueueAsync<TPayload>(
        Guid agentId,
        AgentCommandType type,
        TPayload payload,
        HttpContext http,
        IControlPlaneStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);

        var idempotencyKey = http.Request.Headers["X-YazmaBackup-Idempotency-Key"].FirstOrDefault();
        try
        {
            var command = await store.EnqueueAsync(
                agentId,
                type,
                JsonSerializer.Serialize(payload),
                idempotencyKey,
                ct).ConfigureAwait(false);

            return Results.Accepted(
                $"/api/v1/admin/commands/{command.CommandId}",
                new EnqueueCommandResponse(command.CommandId));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
