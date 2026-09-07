using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace YazmaBackup.ControlPlane;

public sealed record RepositoryVaultKey(string KeyId, byte[] KeyMaterial);

public sealed class RepositoryKeyVault
{
    private readonly string _root;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    public RepositoryKeyVault(string stateRoot, IDataProtectionProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(provider);
        _root = Path.Combine(Path.GetFullPath(stateRoot), "repository-key-vault");
        Directory.CreateDirectory(_root);
        _protector = provider.CreateProtector("YazmaBackup.RepositoryKeyVault.v1");
    }

    public RepositoryVaultKey GetOrCreate(string repositoryId)
    {
        ValidateRepositoryId(repositoryId);
        lock (_gate)
        {
            var path = GetPath(repositoryId);
            if (File.Exists(path))
            {
                var protectedBytes = File.ReadAllBytes(path);
                try
                {
                    var clear = _protector.Unprotect(protectedBytes);
                    if (clear.Length != 32)
                    {
                        CryptographicOperations.ZeroMemory(clear);
                        throw new InvalidDataException($"Protected repository key has invalid length: {repositoryId}.");
                    }
                    return new RepositoryVaultKey("auto-v1", clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }

            var key = RandomNumberGenerator.GetBytes(32);
            var protectedKey = _protector.Protect(key);
            try
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, protectedKey);
                File.Move(temp, path, overwrite: false);
                return new RepositoryVaultKey("auto-v1", key);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
    }

    private string GetPath(string repositoryId)
    {
        var bytes = Encoding.UTF8.GetBytes(repositoryId);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Path.Combine(_root, hash + ".key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateRepositoryId(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (repositoryId.Length > 128 || repositoryId.Any(char.IsControl))
            throw new ArgumentException("Repository id is invalid.", nameof(repositoryId));
    }
}
