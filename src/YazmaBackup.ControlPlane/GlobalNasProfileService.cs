using System.Security.Cryptography;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed class GlobalNasProfileService(
    IControlPlaneStore controlStore,
    GlobalNasProfileStore profileStore)
{
    public async Task<(int Queued, int Skipped)> ApplyToAllAgentsAsync(GlobalNasProfile profile, CancellationToken ct)
    {
        var agents = await controlStore.GetAgentsAsync(ct).ConfigureAwait(false);
        var queued = 0;
        var skipped = 0;
        foreach (var agent in agents)
        {
            if (await ApplyToAgentAsync(profile, agent, ct).ConfigureAwait(false)) queued++;
            else skipped++;
        }
        return (queued, skipped);
    }

    public async Task<bool> ApplyCurrentToAgentAsync(Guid agentId, CancellationToken ct)
    {
        var profile = await profileStore.GetAsync(ct).ConfigureAwait(false);
        if (profile is null) return false;
        var agent = (await controlStore.GetAgentsAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.AgentId == agentId);
        return agent is not null && await ApplyToAgentAsync(profile, agent, ct).ConfigureAwait(false);
    }

    private async Task<bool> ApplyToAgentAsync(GlobalNasProfile profile, AgentRecord agent, CancellationToken ct)
    {
        if (!ValidPublicKey(agent.KeyExchangePublicKeyPem)) return false;

        var clear = JsonSerializer.SerializeToUtf8Bytes(new NasCredentialSecret(profile.Username, profile.Password));
        byte[]? wrapped = null;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(agent.KeyExchangePublicKeyPem);
            wrapped = rsa.Encrypt(clear, RSAEncryptionPadding.OaepSHA256);

            await controlStore.EnqueueAsync(
                agent.AgentId,
                AgentCommandType.ProvisionNasCredential,
                JsonSerializer.Serialize(new WrappedNasCredentialPayload(profile.RepositoryId, Convert.ToBase64String(wrapped))),
                $"global-nas:{profile.Version}:{profile.RepositoryId}:{agent.AgentId:N}",
                ct).ConfigureAwait(false);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    private static bool ValidPublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 8192) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 3072;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
