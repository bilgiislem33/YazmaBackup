using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class AgentKeyExchangeStore
{
    private readonly MachineSecretStore _secretStore = new();

    public string GetOrCreatePublicKeyPem()
    {
        using var rsa = LoadOrCreatePrivateKey();
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    public byte[] UnwrapSecret(string wrappedSecretBase64, int maximumClearBytes = 4096)
    {
        byte[] wrapped;
        try { wrapped = Convert.FromBase64String(wrappedSecretBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Wrapped secret is not valid Base64.", ex); }
        try
        {
            using var rsa = LoadOrCreatePrivateKey();
            var clear = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            if (clear.Length == 0 || clear.Length > maximumClearBytes)
            {
                CryptographicOperations.ZeroMemory(clear);
                throw new InvalidDataException("Unwrapped secret length is outside the supported range.");
            }
            return clear;
        }
        finally { CryptographicOperations.ZeroMemory(wrapped); }
    }

    public byte[] UnwrapRepositoryKey(string wrappedKeyBase64)
    {
        byte[] wrapped;
        try { wrapped = Convert.FromBase64String(wrappedKeyBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Wrapped repository key is not valid Base64.", ex); }
        try
        {
            using var rsa = LoadOrCreatePrivateKey();
            var clear = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
            if (clear.Length != 32)
            {
                CryptographicOperations.ZeroMemory(clear);
                throw new InvalidDataException("Unwrapped repository key is not 32 bytes.");
            }
            return clear;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    private RSA LoadOrCreatePrivateKey()
    {
        var rsa = RSA.Create();
        var privatePem = _secretStore.Read(AgentPaths.KeyExchangePrivateKeyFile);
        if (!string.IsNullOrWhiteSpace(privatePem))
        {
            rsa.ImportFromPem(privatePem);
            return rsa;
        }

        rsa.KeySize = 3072;
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        _secretStore.Write(AgentPaths.KeyExchangePrivateKeyFile, pem);
        return rsa;
    }
}
