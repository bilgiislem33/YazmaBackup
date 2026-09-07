using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class NasCredentialStore
{
    private readonly MachineSecretStore _secretStore = new("YazmaBackup NAS credential");

    public void Provision(string repositoryId, NasCredentialSecret credential)
    {
        Validate(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.Password);
        var json = JsonSerializer.Serialize(credential);
        try { _secretStore.Write(GetPath(repositoryId), json); }
        finally { json = string.Empty; }
    }

    public NasCredentialSecret? Read(string repositoryId)
    {
        Validate(repositoryId);
        var json = _secretStore.Read(GetPath(repositoryId));
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<NasCredentialSecret>(json); }
        finally { json = string.Empty; }
    }

    public static bool Exists(string repositoryId)
    {
        Validate(repositoryId);
        return File.Exists(GetPath(repositoryId));
    }

    private static string GetPath(string repositoryId)
    {
        var bytes = Encoding.UTF8.GetBytes(repositoryId);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Path.Combine(AgentPaths.NasCredentialDirectory, hash + ".dpapi");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Validate(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (repositoryId.Length > 128 || repositoryId.Any(char.IsControl))
            throw new ArgumentException("Repository id is invalid.", nameof(repositoryId));
    }
}
