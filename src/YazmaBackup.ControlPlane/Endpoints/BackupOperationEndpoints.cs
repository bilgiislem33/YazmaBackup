using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class BackupOperationEndpoints
{
    internal static AdminEndpointGroups MapBackupOperationEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Operate.MapPost("/agents/{agentId:guid}/backup", async (
            Guid agentId,
            HttpContext http,
            EnqueueBackupRequest request,
            IControlPlaneStore store,
            RepositoryKeyVault keyVault,
            CancellationToken ct) =>
        {
            var retention = request.Retention ?? new RetentionPolicy();
            var protection = request.Protection ?? new ProtectionPolicy();
            if (!ValidBackupRequest(
                    request.Path,
                    request.RepositoryRoot,
                    request.RepositoryId,
                    request.ActiveBytesPerSecond,
                    request.IdleBytesPerSecond,
                    request.UserIdleThresholdSeconds,
                    retention,
                    protection,
                    out var error))
            {
                return Results.BadRequest(new { error });
            }

            var agent = (await store.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AgentId == agentId);
            if (agent is null) return Results.NotFound();
            if (!AgentRequestSecurity.ValidPublicKey(agent.KeyExchangePublicKeyPem))
                return Results.Conflict(new { error = "Agent repository encryption key bootstrap requires a valid Agent key-exchange public key. Update or re-enroll the Agent." });

            RepositoryVaultKey vaultKey;
            try
            {
                vaultKey = keyVault.GetOrCreate(request.RepositoryId.Trim());
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException or ArgumentException)
            {
                return Results.Problem($"Repository encryption key vault failed: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            byte[] wrapped;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
                wrapped = rsa.Encrypt(vaultKey.KeyMaterial, RSAEncryptionPadding.OaepSHA256);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
                return Results.Conflict(new { error = "Agent public key could not wrap the repository encryption key." });
            }

            try
            {
                var requestIdempotency = http.Request.Headers["X-YazmaBackup-Idempotency-Key"].FirstOrDefault();
                var keyIdempotency = string.IsNullOrWhiteSpace(requestIdempotency)
                    ? null
                    : requestIdempotency + ":repository-key";

                var keyCommand = await store.EnqueueAsync(
                    agentId,
                    AgentCommandType.ProvisionRepositoryKey,
                    JsonSerializer.Serialize(new WrappedRepositoryKeyPayload(
                        request.RepositoryId.Trim(),
                        vaultKey.KeyId,
                        Convert.ToBase64String(wrapped),
                        true)),
                    keyIdempotency,
                    ct).ConfigureAwait(false);

                var backupCommand = await store.EnqueueAsync(
                    agentId,
                    AgentCommandType.BackupPath,
                    JsonSerializer.Serialize(new BackupPayload(
                        request.Path,
                        request.RepositoryRoot,
                        request.RepositoryId,
                        request.RequireSnapshot,
                        request.ActiveBytesPerSecond,
                        request.IdleBytesPerSecond,
                        request.UserIdleThresholdSeconds,
                        retention,
                        protection)),
                    requestIdempotency,
                    ct).ConfigureAwait(false);

                return Results.Accepted(
                    $"/api/v1/admin/commands/{backupCommand.CommandId}",
                    new
                    {
                        commandId = backupCommand.CommandId,
                        repositoryKeyCommandId = keyCommand.CommandId,
                        repositoryKeyId = vaultKey.KeyId,
                        repositoryKeyProvisioning = "queued-before-backup"
                    });
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
            finally
            {
                CryptographicOperations.ZeroMemory(vaultKey.KeyMaterial);
                CryptographicOperations.ZeroMemory(wrapped);
            }
        });

        groups.Backup.MapPost("/agents/{agentId:guid}/scrub", async (
            Guid agentId,
            HttpContext http,
            EnqueueScrubRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) || !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Invalid repository scrub request." });

            return await AdminCommandQueue.EnqueueAsync(
                agentId,
                AgentCommandType.ScrubRepository,
                new ScrubRepositoryPayload(request.RepositoryRoot, request.RepositoryId, request.MigrateLegacyPlaintext),
                http,
                store,
                ct).ConfigureAwait(false);
        });

        groups.Backup.MapPost("/agents/{agentId:guid}/restore-drill", async (
            Guid agentId,
            HttpContext http,
            EnqueueRestoreDrillRequest request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (!ValidPath(request.RepositoryRoot) ||
                !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128) ||
                !ValidPath(request.SourceRoot))
            {
                return Results.BadRequest(new { error = "Restore drill repository or source path is invalid." });
            }

            return await AdminCommandQueue.EnqueueAsync(
                agentId,
                AgentCommandType.RestoreDrill,
                new RestoreDrillPayload(request.RepositoryRoot, request.RepositoryId, request.SourceRoot),
                http,
                store,
                ct).ConfigureAwait(false);
        });

        groups.Backup.MapPost("/agents/{agentId:guid}/repository-health", async (
            Guid agentId,
            HttpContext http,
            RepositoryHealthScanPayload request,
            IControlPlaneStore store,
            CancellationToken ct) =>
        {
            if (request.PolicyId == Guid.Empty ||
                !ValidPath(request.RepositoryRoot) ||
                !AgentRequestSecurity.ValidIdentifier(request.RepositoryId, 128))
            {
                return Results.BadRequest(new { error = "Repository health request is invalid." });
            }

            return await AdminCommandQueue.EnqueueAsync(
                agentId,
                AgentCommandType.RepositoryHealthScan,
                request,
                http,
                store,
                ct).ConfigureAwait(false);
        });

        return groups;
    }

    private static bool ValidBackupRequest(
        string path,
        string repositoryRoot,
        string repositoryId,
        long activeRate,
        long idleRate,
        int idleThresholdSeconds,
        RetentionPolicy retention,
        ProtectionPolicy protection,
        out string? error)
    {
        error = null;
        if (!ValidPath(path) || !ValidPath(repositoryRoot) || !AgentRequestSecurity.ValidIdentifier(repositoryId, 128))
        {
            error = "Path, repositoryRoot or repositoryId is invalid.";
            return false;
        }

        if (activeRate is < 0 or > 1024L * 1024 * 1024 || idleRate is < 0 or > 1024L * 1024 * 1024)
        {
            error = "Bandwidth limits must be between 0 and 1 GiB/s. 0 means unlimited.";
            return false;
        }

        if (idleThresholdSeconds is < 30 or > 86400)
        {
            error = "User idle threshold must be 30..86400 seconds.";
            return false;
        }

        try
        {
            retention.Validate();
            protection.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    private static bool ValidPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;
}
