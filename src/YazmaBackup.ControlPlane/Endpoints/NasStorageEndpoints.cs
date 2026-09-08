using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class NasStorageEndpoints
{
    internal static AdminEndpointGroups MapNasStorageEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet(NasStorageEndpointContracts.GlobalProfile, async (
            GlobalNasProfileStore profiles,
            CancellationToken ct) =>
        {
            var profile = await profiles.GetAsync(ct).ConfigureAwait(false);
            return profile is null
                ? Results.NoContent()
                : Results.Ok(GlobalNasProfileStore.ToSummary(profile));
        });

        groups.Security.MapPost(NasStorageEndpointContracts.GlobalProfile, async (
            SaveGlobalNasProfileRequest request,
            GlobalNasProfileStore profiles,
            GlobalNasProfileService profileService,
            CancellationToken ct) =>
        {
            if (!AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                !ValidPathInput(request.RepositoryRoot) ||
                !AgentRequestSecurity.ValidText(request.Username, 128))
            {
                return Results.BadRequest(new { error = "Repository, NAS yolu veya kullanıcı adı geçersiz." });
            }

            var existing = await profiles.GetAsync(ct).ConfigureAwait(false);
            var password = request.Password;
            if (string.IsNullOrEmpty(password))
            {
                if (existing is null || string.IsNullOrEmpty(existing.Password))
                    return Results.BadRequest(new { error = "İlk global NAS kaydında parola zorunludur." });
                password = existing.Password;
            }

            if (password.Length > 512)
                return Results.BadRequest(new { error = "NAS parolası çok uzun." });

            var saved = await profiles.SaveAsync(
                request.RepositoryId,
                request.RepositoryRoot,
                request.Username,
                password,
                ct).ConfigureAwait(false);
            var applied = await profileService.ApplyToAllAgentsAsync(saved, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                profile = GlobalNasProfileStore.ToSummary(saved),
                queuedAgents = applied.Queued,
                skippedAgents = applied.Skipped,
                passwordReused = string.IsNullOrEmpty(request.Password)
            });
        });

        groups.Security.MapPost(NasStorageEndpointContracts.SaveAndTestCredential, async (
            Guid agentId,
            HttpContext http,
            ProvisionAndTestNasCredentialRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!ValidPathInput(request.RepositoryRoot) ||
                !request.RepositoryRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
                !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                string.IsNullOrWhiteSpace(request.Username) ||
                request.Username.Length > 128 ||
                string.IsNullOrWhiteSpace(request.Password) ||
                request.Password.Length > 128 ||
                request.Username.Any(char.IsControl) ||
                request.Password.Any(char.IsControl))
            {
                return Results.BadRequest(new { error = "NAS yolu, repository, kullanıcı adı veya şifre geçersiz." });
            }

            var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(a => a.AgentId == agentId);
            if (agent is null) return Results.NotFound();
            if (!AgentRequestSecurity.ValidPublicKey(agent.KeyExchangePublicKeyPem))
            {
                return Results.Conflict(new
                {
                    error = "Agent RSA key-exchange anahtarı yok. Agent güncellenmeli veya yeniden kaydedilmelidir."
                });
            }

            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(
                new NasCredentialSecret(request.Username.Trim(), request.Password));
            if (clear.Length > 280)
            {
                CryptographicOperations.ZeroMemory(clear);
                return Results.BadRequest(new { error = "NAS kullanıcı adı/şifre kombinasyonu güvenli taşıma sınırını aşıyor." });
            }

            byte[] wrapped;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                wrapped = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);
            }
            catch (CryptographicException)
            {
                return Results.Conflict(new { error = "Agent public key NAS kimlik bilgisini şifreleyemedi." });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }

            try
            {
                return await AdminCommandQueue.EnqueueAsync(
                    agentId,
                    AgentCommandType.ProvisionAndTestNasCredential,
                    new WrappedProvisionAndTestNasCredentialPayload(
                        request.RepositoryRoot.Trim(),
                        request.RepositoryId.Trim(),
                        Convert.ToBase64String(wrapped)),
                    http,
                    store,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        });

        groups.Security.MapPost(NasStorageEndpointContracts.ProvisionCredential, async (
            Guid agentId,
            HttpContext http,
            ProvisionNasCredentialRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                string.IsNullOrWhiteSpace(request.Username) ||
                request.Username.Length > 128 ||
                string.IsNullOrWhiteSpace(request.Password) ||
                request.Password.Length > 128 ||
                request.Username.Any(char.IsControl) ||
                request.Password.Any(char.IsControl))
            {
                return Results.BadRequest(new { error = "Repository, NAS username or password is invalid." });
            }

            var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(a => a.AgentId == agentId);
            if (agent is null) return Results.NotFound();
            if (!AgentRequestSecurity.ValidPublicKey(agent.KeyExchangePublicKeyPem))
            {
                return Results.Conflict(new
                {
                    error = "Agent has no valid key-exchange public key. Upgrade or re-enroll the Agent."
                });
            }

            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(
                new NasCredentialSecret(request.Username.Trim(), request.Password));
            if (clear.Length > 280)
            {
                CryptographicOperations.ZeroMemory(clear);
                return Results.BadRequest(new { error = "NAS credential is too long for secure transport." });
            }

            byte[] wrapped;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                wrapped = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);
            }
            catch (CryptographicException)
            {
                return Results.Conflict(new { error = "Agent public key could not encrypt the NAS credential." });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }

            try
            {
                return await AdminCommandQueue.EnqueueAsync(
                    agentId,
                    AgentCommandType.ProvisionNasCredential,
                    new WrappedNasCredentialPayload(
                        request.RepositoryId.Trim(),
                        Convert.ToBase64String(wrapped)),
                    http,
                    store,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        });

        groups.Operate.MapPost(NasStorageEndpointContracts.TestAccess, async (
            Guid agentId,
            HttpContext http,
            TestNasAccessRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!ValidPathInput(request.RepositoryRoot) ||
                !request.RepositoryRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
                !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128))
            {
                return Results.BadRequest(new
                {
                    error = "NAS test requires a valid UNC repository path and repository id."
                });
            }

            return await AdminCommandQueue.EnqueueAsync(
                agentId,
                AgentCommandType.TestNasAccess,
                new TestNasAccessPayload(
                    request.RepositoryRoot.Trim(),
                    request.RepositoryId.Trim()),
                http,
                store,
                ct).ConfigureAwait(false);
        });

        return groups;
    }

    private static bool ValidPathInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
}
