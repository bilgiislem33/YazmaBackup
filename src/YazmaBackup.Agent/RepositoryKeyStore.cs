using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using YazmaBackup.Infrastructure;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class RepositoryKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly MachineSecretStore _secretStore = new();

    public RepositoryCryptoContext OpenCryptoContext(string repositoryId)
        => OpenCryptoContext(repositoryId, out _);

    public RepositoryCryptoContext OpenCryptoContext(string repositoryId, out string canonicalRepositoryId)
    {
        var document = ReadDocument(repositoryId) ?? throw new InvalidOperationException($"Repository key ring is not provisioned: {repositoryId}.");
        canonicalRepositoryId = document.RepositoryId;
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var item in document.Keys)
            {
                var bytes = Convert.FromBase64String(item.KeyBase64);
                if (bytes.Length != 32) throw new InvalidDataException($"Repository key {item.KeyId} is not 32 bytes.");
                keys[item.KeyId] = bytes;
            }
            return new RepositoryCryptoContext(document.ActiveKeyId, keys);
        }
        finally
        {
            foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Provision(string repositoryId, string keyId, string keyBase64, bool makeActive)
    {
        byte[] key;
        try { key = Convert.FromBase64String(keyBase64); }
        catch (FormatException ex) { throw new ArgumentException("Repository key must be Base64 encoded.", nameof(keyBase64), ex); }
        try { Provision(repositoryId, keyId, key, makeActive); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Provision(string repositoryId, string keyId, ReadOnlySpan<byte> keyMaterial, bool makeActive)
    {
        ValidateIdentifier(repositoryId, nameof(repositoryId));
        ValidateIdentifier(keyId, nameof(keyId));
        if (keyMaterial.Length != 32)
            throw new ArgumentException("Repository key must be exactly 32 bytes.", nameof(keyMaterial));
        var key = keyMaterial.ToArray();
        try
        {
            var existing = ReadDocument(repositoryId);
            var keys = existing?.Keys.ToList() ?? [];
            var sameId = keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (sameId is not null)
            {
                var existingBytes = Convert.FromBase64String(sameId.KeyBase64);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(existingBytes, key))
                        throw new InvalidOperationException($"Key id {keyId} already exists with different key material.");
                }
                finally { CryptographicOperations.ZeroMemory(existingBytes); }
            }
            else
            {
                keys.Add(new RepositoryKeyRecord(keyId, Convert.ToBase64String(key), DateTimeOffset.UtcNow));
            }

            var activeKeyId = existing is null || makeActive ? keyId : existing.ActiveKeyId;
            var document = new RepositoryKeyRingDocument(existing?.RepositoryId ?? repositoryId, activeKeyId, keys.OrderBy(k => k.CreatedAtUtc).ToArray());
            WriteDocument(repositoryId, document);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Activate(string repositoryId, string keyId)
    {
        var document = ReadDocument(repositoryId) ?? throw new InvalidOperationException($"Repository key ring is not provisioned: {repositoryId}.");
        if (!document.Keys.Any(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Repository key not found: {keyId}.");
        WriteDocument(repositoryId, document with { ActiveKeyId = keyId });
    }

    public async Task RemoveAfterRepositoryValidationAsync(string repositoryRoot, string repositoryId, string keyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        using var crypto = OpenCryptoContext(repositoryId, out var canonicalRepositoryId);
        var repository = new FileSystemBackupRepository(repositoryRoot, canonicalRepositoryId, crypto);
        var referencedKeys = await repository.GetReferencedEncryptionKeyIdsAsync(cancellationToken).ConfigureAwait(false);
        if (referencedKeys.Contains(keyId))
            throw new InvalidOperationException($"Repository still references key {keyId}. Complete scrub/rekey before retiring it.");
        Remove(repositoryId, keyId);
    }

    private void Remove(string repositoryId, string keyId)
    {
        var document = ReadDocument(repositoryId) ?? throw new InvalidOperationException($"Repository key ring is not provisioned: {repositoryId}.");
        if (string.Equals(document.ActiveKeyId, keyId, StringComparison.Ordinal))
            throw new InvalidOperationException("Active repository key cannot be removed. Activate a replacement key first.");
        var keys = document.Keys.Where(k => !string.Equals(k.KeyId, keyId, StringComparison.Ordinal)).ToArray();
        if (keys.Length == document.Keys.Length) throw new KeyNotFoundException($"Repository key not found: {keyId}.");
        WriteDocument(repositoryId, document with { Keys = keys });
    }

    public RepositoryKeySummary GetSummary(string repositoryId)
    {
        var document = ReadDocument(repositoryId) ?? throw new InvalidOperationException($"Repository key ring is not provisioned: {repositoryId}.");
        return new RepositoryKeySummary(document.RepositoryId, document.ActiveKeyId, document.Keys.Select(k => k.KeyId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private RepositoryKeyRingDocument? ReadDocument(string repositoryId)
    {
        ValidateIdentifier(repositoryId, nameof(repositoryId));
        var value = _secretStore.Read(GetPath(repositoryId));
        if (string.IsNullOrWhiteSpace(value)) return null;
        var document = JsonSerializer.Deserialize<RepositoryKeyRingDocument>(value, JsonOptions)
            ?? throw new InvalidDataException("Repository key ring could not be deserialized.");
        if (!string.Equals(document.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Repository key ring identifier mismatch.");
        if (document.Keys.Length == 0 || !document.Keys.Any(k => string.Equals(k.KeyId, document.ActiveKeyId, StringComparison.Ordinal)))
            throw new InvalidDataException("Repository key ring has no valid active key.");
        return document;
    }

    private void WriteDocument(string repositoryId, RepositoryKeyRingDocument document)
    {
        _secretStore.Write(GetPath(repositoryId), JsonSerializer.Serialize(document, JsonOptions));
    }

    private static string GetPath(string repositoryId) => Path.Combine(AgentPaths.RepositoryKeyDirectory, repositoryId + ".dpapi");

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Identifier contains unsupported characters.", parameterName);
    }

    private sealed record RepositoryKeyRingDocument(string RepositoryId, string ActiveKeyId, RepositoryKeyRecord[] Keys);
    private sealed record RepositoryKeyRecord(string KeyId, string KeyBase64, DateTimeOffset CreatedAtUtc);
}

public sealed record RepositoryKeySummary(string RepositoryId, string ActiveKeyId, IReadOnlyList<string> KeyIds);
