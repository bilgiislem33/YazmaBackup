using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class RepositoryKeyEndpoints
{
    internal const string Provision = "/agents/{agentId:guid}/repository-key";
    internal const string Remove = "/agents/{agentId:guid}/repository-key/remove";

    internal static AdminEndpointGroups MapRepositoryKeyEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Security.MapPost(Provision, async (
            Guid agentId,
            HttpContext http,
            ProvisionRepositoryKeyRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                !AgentRequestSecurity.ValidIdentifier(request.KeyId, 128) ||
                string.IsNullOrWhiteSpace(request.KeyBase64) || request.KeyBase64.Length > 256)
            {
                return Results.BadRequest(new { error = "Invalid repository key provisioning request." });
            }

            var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
            if (agent is null) return Results.NotFound();
            if (!AgentRequestSecurity.ValidPublicKey(agent.KeyExchangePublicKeyPem))
                return Results.Conflict(new { error = "Agent has no valid key-exchange public key. Upgrade or re-enroll the Agent." });

            byte[] clearKey;
            try { clearKey = Convert.FromBase64String(request.KeyBase64); }
            catch (FormatException) { return Results.BadRequest(new { error = "Repository key is not valid Base64." }); }

            if (clearKey.Length != 32)
            {
                CryptographicOperations.ZeroMemory(clearKey);
                return Results.BadRequest(new { error = "Repository key must decode to exactly 32 bytes." });
            }

            byte[] wrapped;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                wrapped = rsa.Encrypt(clearKey, RSAEncryptionPadding.OaepSHA256);
            }
            catch (CryptographicException)
            {
                return Results.Conflict(new { error = "Agent key-exchange public key could not encrypt the repository key." });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearKey);
            }

            try
            {
                return await AdminCommandQueue.EnqueueAsync(
                    agentId,
                    AgentCommandType.ProvisionRepositoryKey,
                    new WrappedRepositoryKeyPayload(request.RepositoryId, request.KeyId, Convert.ToBase64String(wrapped), request.MakeActive),
                    http,
                    store,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        });

        groups.Security.MapPost(Remove, async (
            Guid agentId,
            HttpContext http,
            RemoveRepositoryKeyRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!ValidPathInput(request.RepositoryRoot) ||
                !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                !AgentRequestSecurity.ValidIdentifier(request.KeyId, 128))
            {
                return Results.BadRequest(new { error = "Invalid repository key removal request." });
            }

            return await AdminCommandQueue.EnqueueAsync(
                agentId,
                AgentCommandType.RemoveRepositoryKey,
                new RemoveRepositoryKeyPayload(request.RepositoryRoot, request.RepositoryId, request.KeyId),
                http,
                store,
                ct).ConfigureAwait(false);
        });

        return groups;
    }

    private static bool ValidPathInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
}
