using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AgentCommandEndpoints
{
    internal static IEndpointRouteBuilder MapAgentCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(AgentEndpointContracts.NextCommand, async (HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            var auth = await AgentRequestSecurity.AuthenticateAsync(http, store, ct).ConfigureAwait(false);
            if (auth is null) return Results.Unauthorized();

            var cmd = await store.ClaimNextAsync(auth.Value.AgentId, ct).ConfigureAwait(false);
            if (cmd is null) return Results.NoContent();
            if (cmd.LeaseId is null || cmd.LeaseExpiresAtUtc is null)
                return Results.Problem("Command lease invariant failed.");

            return Results.Ok(new AgentCommandDto(
                cmd.CommandId,
                cmd.Type.ToString(),
                cmd.PayloadJson,
                cmd.CreatedAtUtc,
                cmd.LeaseId.Value,
                cmd.LeaseExpiresAtUtc.Value,
                cmd.AttemptCount));
        });

        endpoints.MapPost(AgentEndpointContracts.RenewCommandLease, async (
            Guid commandId,
            HttpContext http,
            RenewCommandLeaseRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            var auth = await AgentRequestSecurity.AuthenticateAsync(http, store, ct).ConfigureAwait(false);
            if (auth is null) return Results.Unauthorized();

            try
            {
                var expires = await store.RenewLeaseAsync(auth.Value.AgentId, commandId, request.LeaseId, ct).ConfigureAwait(false);
                return Results.Ok(new RenewCommandLeaseResponse(request.LeaseId, expires));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        endpoints.MapPost(AgentEndpointContracts.CompleteCommand, async (
            Guid commandId,
            HttpContext http,
            CommandResultRequest request,
            IControlPlaneStore store,
            RepositoryKeyVault keyVault,
            CancellationToken ct) =>
        {
            var auth = await AgentRequestSecurity.AuthenticateAsync(http, store, ct).ConfigureAwait(false);
            if (auth is null) return Results.Unauthorized();
            if (request.ResultJson is null || request.ResultJson.Length > 4 * 1024 * 1024 || (request.Error?.Length ?? 0) > 4096)
                return Results.BadRequest(new { error = "Command result is too large." });

            try
            {
                var completedCommand = await store.GetCommandAsync(commandId, ct).ConfigureAwait(false);
                await store.CompleteAsync(
                    auth.Value.AgentId,
                    commandId,
                    request.LeaseId,
                    request.Succeeded,
                    request.ResultJson,
                    request.Error,
                    ct).ConfigureAwait(false);

                // R6.8 legacy/existing-policy self-heal: if a BackupPath reached an Agent
                // before its repository key ring existed, provision the key once and requeue
                // that exact backup payload. Preserve idempotency and zero key material after use.
                if (!request.Succeeded &&
                    completedCommand is not null &&
                    completedCommand.Type == AgentCommandType.BackupPath &&
                    completedCommand.AgentId == auth.Value.AgentId &&
                    !string.IsNullOrWhiteSpace(request.Error) &&
                    request.Error.Contains("Repository key ring is not provisioned", StringComparison.OrdinalIgnoreCase) &&
                    !(completedCommand.IdempotencyKey?.StartsWith("autokey:", StringComparison.Ordinal) ?? false))
                {
                    var payload = JsonSerializer.Deserialize<BackupPayload>(completedCommand.PayloadJson);
                    if (payload is not null)
                    {
                        var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false))
                            .FirstOrDefault(a => a.AgentId == auth.Value.AgentId);
                        if (agent is not null && AgentRequestSecurity.ValidPublicKey(agent.KeyExchangePublicKeyPem))
                        {
                            var vaultKey = keyVault.GetOrCreate(payload.RepositoryId.Trim());
                            byte[]? wrapped = null;
                            try
                            {
                                using var rsa = RSA.Create();
                                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                                wrapped = rsa.Encrypt(vaultKey.KeyMaterial, RSAEncryptionPadding.OaepSHA256);

                                await store.EnqueueAsync(
                                    auth.Value.AgentId,
                                    AgentCommandType.ProvisionRepositoryKey,
                                    JsonSerializer.Serialize(new WrappedRepositoryKeyPayload(
                                        payload.RepositoryId.Trim(),
                                        vaultKey.KeyId,
                                        Convert.ToBase64String(wrapped),
                                        true)),
                                    $"autokey:{commandId:N}:provision",
                                    ct).ConfigureAwait(false);

                                await store.EnqueueAsync(
                                    auth.Value.AgentId,
                                    AgentCommandType.BackupPath,
                                    completedCommand.PayloadJson,
                                    $"autokey:{commandId:N}:retry",
                                    ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
                                if (wrapped is not null)
                                    CryptographicOperations.ZeroMemory(wrapped);
                            }
                        }
                    }
                }

                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        return endpoints;
    }
}
