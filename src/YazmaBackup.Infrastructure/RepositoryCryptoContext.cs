using System.Security.Cryptography;
using System.Text;

namespace YazmaBackup.Infrastructure;

public sealed class RepositoryCryptoContext : IDisposable
{
    private static readonly byte[] Magic = "YBENC301"u8.ToArray();
    private readonly Dictionary<string, byte[]> _keys;

    public RepositoryCryptoContext(string activeKeyId, ReadOnlySpan<byte> key)
    {
        ValidateKeyId(activeKeyId);
        if (key.Length != 32) throw new ArgumentException("Repository key is not 32 bytes.", nameof(key));
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [activeKeyId] = key.ToArray()
        };
        KeyId = activeKeyId;
    }

    public RepositoryCryptoContext(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        ValidateKeyId(activeKeyId);
        if (keys.Count == 0) throw new ArgumentException("At least one repository key is required.", nameof(keys));
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in keys)
            {
                ValidateKeyId(pair.Key);
                if (pair.Value.Length != 32) throw new ArgumentException($"Repository key {pair.Key} is not 32 bytes.", nameof(keys));
                _keys[pair.Key] = pair.Value.ToArray();
            }
            if (!_keys.ContainsKey(activeKeyId)) throw new ArgumentException("Active repository key is not present in the key ring.", nameof(activeKeyId));
            KeyId = activeKeyId;
        }
        catch
        {
            foreach (var copiedKey in _keys.Values) CryptographicOperations.ZeroMemory(copiedKey);
            _keys.Clear();
            throw;
        }
    }

    public string KeyId { get; }
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;
    public bool HasKey(string? keyId) => keyId is not null && _keys.ContainsKey(keyId);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string associatedData)
    {
        var key = _keys[KeyId];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = GC.AllocateUninitializedArray<byte>(plaintext.Length);
        var aad = Encoding.UTF8.GetBytes(associatedData);
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            var keyIdBytes = Encoding.UTF8.GetBytes(KeyId);
            var output = GC.AllocateUninitializedArray<byte>(Magic.Length + 2 + keyIdBytes.Length + nonce.Length + tag.Length + ciphertext.Length);
            var offset = 0;
            Magic.CopyTo(output.AsSpan(offset)); offset += Magic.Length;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset, 2), checked((ushort)keyIdBytes.Length)); offset += 2;
            keyIdBytes.CopyTo(output.AsSpan(offset)); offset += keyIdBytes.Length;
            nonce.CopyTo(output.AsSpan(offset)); offset += nonce.Length;
            tag.CopyTo(output.AsSpan(offset)); offset += tag.Length;
            ciphertext.CopyTo(output.AsSpan(offset));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelope, string associatedData)
    {
        if (!IsEncrypted(envelope)) throw new InvalidDataException("Encrypted repository envelope header is missing.");
        var offset = Magic.Length;
        if (envelope.Length < offset + 2) throw new InvalidDataException("Encrypted repository envelope is truncated.");
        var keyIdLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(envelope.Slice(offset, 2)); offset += 2;
        if (envelope.Length < offset + keyIdLength + 12 + 16) throw new InvalidDataException("Encrypted repository envelope is truncated.");
        var keyId = Encoding.UTF8.GetString(envelope.Slice(offset, keyIdLength)); offset += keyIdLength;
        if (!_keys.TryGetValue(keyId, out var key))
            throw new CryptographicException($"Repository key {keyId} is not available in the Agent key ring.");
        var nonce = envelope.Slice(offset, 12); offset += 12;
        var tag = envelope.Slice(offset, 16); offset += 16;
        var ciphertext = envelope[offset..];
        var plaintext = GC.AllocateUninitializedArray<byte>(ciphertext.Length);
        var aad = Encoding.UTF8.GetBytes(associatedData);
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> content) =>
        content.Length >= Magic.Length && content[..Magic.Length].SequenceEqual(Magic);

    public static string? ReadKeyId(ReadOnlySpan<byte> envelope)
    {
        if (!IsEncrypted(envelope) || envelope.Length < Magic.Length + 2) return null;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(envelope.Slice(Magic.Length, 2));
        var offset = Magic.Length + 2;
        if (envelope.Length < offset + length) return null;
        return Encoding.UTF8.GetString(envelope.Slice(offset, length));
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128 || keyId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Repository encryption key id is invalid.", nameof(keyId));
    }
}
