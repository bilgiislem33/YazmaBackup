using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class AgentUpdateStager(AgentConfig config)
{
    private static readonly JsonSerializerOptions ReadyJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<StageUpdateResultDto> StageAsync(StageAgentUpdatePayload payload, CancellationToken cancellationToken)
    {
        ValidateVersion(payload.Version);
        ValidateSha256(payload.Sha256);
        var current = Version.Parse(AgentWorker.AgentVersion);
        var requested = Version.Parse(payload.Version);
        if (requested <= current)
            throw new InvalidOperationException($"Update version {requested} must be newer than installed version {current}.");

        if (!Uri.TryCreate(payload.PackageUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile))
            throw new InvalidDataException("Update URI must use https:// or file://.");
        if (!File.Exists(config.UpdatePublicKeyPath))
            throw new FileNotFoundException("Update verification public key is not installed.", config.UpdatePublicKeyPath);

        var versionDirectory = Path.Combine(AgentPaths.UpdateDirectory, payload.Version);
        Directory.CreateDirectory(versionDirectory);
        var temp = Path.Combine(versionDirectory, "agent-package.download-" + Guid.NewGuid().ToString("N") + ".tmp");
        var final = Path.Combine(versionDirectory, "agent-package.zip");
        try
        {
            if (uri.Scheme == Uri.UriSchemeFile)
            {
                File.Copy(uri.LocalPath, temp, overwrite: false);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var actualHash = await ComputeSha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update package SHA-256 mismatch. Expected {payload.Sha256}, got {actualHash}.");

            VerifySignature(payload.Version, actualHash, payload.SignatureBase64, config.UpdatePublicKeyPath);
            File.Move(temp, final, overwrite: true);

            var ready = new
            {
                schemaVersion = "1",
                version = payload.Version,
                packagePath = final,
                sha256 = actualHash,
                signatureVerified = true,
                stagedAtUtc = DateTimeOffset.UtcNow
            };
            var readyPath = Path.Combine(versionDirectory, "update-ready.json");
            var readyTemp = readyPath + ".tmp";
            File.WriteAllText(readyTemp, JsonSerializer.Serialize(ready, ReadyJsonOptions));
            File.Move(readyTemp, readyPath, overwrite: true);
            return new StageUpdateResultDto(payload.Version, final, actualHash, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void VerifySignature(string version, string sha256, string signatureBase64, string publicKeyPath)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Update signature is not valid Base64.", ex); }

        var signedData = Encoding.UTF8.GetBytes(version + "\n" + sha256.ToLowerInvariant() + "\n");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(publicKeyPath));
        if (!ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256))
            throw new CryptographicException("Update package signature verification failed.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateVersion(string value)
    {
        if (!Version.TryParse(value, out _) || value.Length > 64)
            throw new InvalidDataException("Update version is invalid.");
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Update SHA-256 is invalid.");
    }
}
