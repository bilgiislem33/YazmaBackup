using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Application;
using YazmaBackup.Domain;

namespace YazmaBackup.Infrastructure;

public sealed class FileSystemBackupRepository : IBackupRepository, IRetentionSafeRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string _repositoryId;
    private readonly RepositoryCryptoContext? _crypto;
    private readonly IThroughputLimiter _limiter;
    private readonly ITransferObserver? _transferObserver;
    private readonly string _metadataJsonPath;
    private readonly string _metadataEncryptedPath;
    private readonly string _quarantineRoot;
    private readonly string _mutationLockPath;
    private readonly bool _allowLegacyInitialization;
    private RepositoryMetadata _metadata;

    public FileSystemBackupRepository(
        string root,
        string repositoryId = "default",
        RepositoryCryptoContext? crypto = null,
        IThroughputLimiter? limiter = null,
        bool allowLegacyInitialization = false,
        ITransferObserver? transferObserver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ValidateIdentifier(repositoryId, nameof(repositoryId));
        _root = Path.GetFullPath(root);
        _repositoryId = repositoryId;
        _crypto = crypto;
        _limiter = limiter ?? new UnlimitedThroughputLimiter();
        _transferObserver = transferObserver;
        _allowLegacyInitialization = allowLegacyInitialization;
        Directory.CreateDirectory(Path.Combine(_root, "chunks"));
        Directory.CreateDirectory(Path.Combine(_root, "manifests"));
        _quarantineRoot = Path.Combine(_root, "quarantine", "manifests");
        Directory.CreateDirectory(_quarantineRoot);
        _mutationLockPath = Path.Combine(_root, ".repository-mutation.lock");
        _metadataJsonPath = Path.Combine(_root, "repository.json");
        _metadataEncryptedPath = Path.Combine(_root, "repository.meta");
        _metadata = InitializeMetadata();
    }

    public string? EncryptionKeyId => _crypto?.KeyId;

    public async Task<bool> ContainsChunkAsync(string sha256, CancellationToken cancellationToken)
    {
        ValidateSha256(sha256);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetChunkPath(sha256);
        if (!File.Exists(path)) return false;

        var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] clear;
        var encrypted = RepositoryCryptoContext.IsEncrypted(stored);
        string? storedKeyId = null;
        try
        {
            if (encrypted)
            {
                if (_crypto is null) throw new CryptographicException("Repository is encrypted but no repository key was supplied.");
                storedKeyId = RepositoryCryptoContext.ReadKeyId(stored);
                if (!_crypto.HasKey(storedKeyId))
                    throw new CryptographicException($"Repository chunk requires unavailable key id {storedKeyId ?? "<unknown>"}.");
                clear = _crypto.Decrypt(stored, ChunkAad(sha256));
            }
            else
            {
                EnsurePlaintextAllowedForLegacyMigration($"chunk {sha256}");
                clear = stored.ToArray();
            }

            try
            {
                var actual = Convert.ToHexString(SHA256.HashData(clear)).ToLowerInvariant();
                if (!string.Equals(actual, sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Existing repository chunk integrity failure: {sha256}.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }

            // A chunk encrypted with an older key is valid but must be rewritten with the active key.
            return _crypto is null || (encrypted && string.Equals(storedKeyId, _crypto.KeyId, StringComparison.Ordinal));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    public async Task PutChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ValidateSha256(sha256);
        var actual = Convert.ToHexString(SHA256.HashData(data.Span)).ToLowerInvariant();
        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Chunk hash does not match its content.");

        var path = GetChunkPath(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] clearExisting;
            try
            {
                if (RepositoryCryptoContext.IsEncrypted(existing))
                {
                    if (_crypto is null) throw new CryptographicException("Encrypted repository chunk requires a repository key.");
                    var requiredKey = RepositoryCryptoContext.ReadKeyId(existing);
                    if (!_crypto.HasKey(requiredKey))
                        throw new CryptographicException($"Repository chunk requires unavailable key id {requiredKey ?? "<unknown>"}.");
                    clearExisting = _crypto.Decrypt(existing, ChunkAad(sha256));
                }
                else
                {
                    EnsurePlaintextAllowedForLegacyMigration($"chunk {sha256}");
                    clearExisting = existing.ToArray();
                }

                try
                {
                    var clearHash = Convert.ToHexString(SHA256.HashData(clearExisting)).ToLowerInvariant();
                    if (!string.Equals(clearHash, sha256, StringComparison.Ordinal))
                        throw new InvalidDataException($"Existing repository chunk integrity failure: {sha256}.");
                    if (_crypto is null) return;
                    await WriteChunkAtomicallyAsync(path, sha256, clearExisting, cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(clearExisting); }
                return;
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }

        await WriteChunkAtomicallyAsync(path, sha256, data, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteManifestAsync(BackupManifest manifest, CancellationToken cancellationToken) =>
        WriteManifestCoreAsync(manifest, allowRewrite: false, cancellationToken);

    private async Task WriteManifestCoreAsync(BackupManifest manifest, bool allowRewrite, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(manifest.AgentId, manifest.BackupId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clear = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
        byte[] stored;
        try
        {
            stored = _crypto is null ? clear.ToArray() : _crypto.Encrypt(clear, ManifestAad(manifest.AgentId, manifest.BackupId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant();
            if (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                try
                {
                    var existingDigest = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
                    if (string.Equals(existingDigest, digest, StringComparison.Ordinal))
                        return;
                    if (!allowRewrite)
                        throw new InvalidDataException($"Append-only manifest violation for backup id {manifest.BackupId}.");
                }
                finally { CryptographicOperations.ZeroMemory(existing); }
            }
            await AtomicWriteAsync(path, stored, cancellationToken).ConfigureAwait(false);
            await AtomicWriteAsync(path + ".sha256", Encoding.ASCII.GetBytes(digest + "\n"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    public Task<BackupManifest> ReadManifestAsync(string agentId, string backupId, CancellationToken cancellationToken) =>
        ReadManifestCoreAsync(agentId, backupId, ResolveManifestPath(agentId, backupId), allowUpgrade: true, cancellationToken);

    private async Task<BackupManifest> ReadManifestCoreAsync(
        string agentId,
        string backupId,
        string path,
        bool allowUpgrade,
        CancellationToken cancellationToken)
    {
        var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var digestPath = path + ".sha256";
        var encrypted = RepositoryCryptoContext.IsEncrypted(stored);
        if (!encrypted) EnsurePlaintextAllowedForLegacyMigration($"manifest {backupId}");

        if (File.Exists(digestPath))
        {
            var expected = (await File.ReadAllTextAsync(digestPath, cancellationToken).ConfigureAwait(false)).Trim();
            ValidateSha256(expected);
            var actual = Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Manifest integrity verification failed.");
        }
        else if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest integrity sidecar is missing.");
        }

        byte[] clear;
        if (encrypted)
        {
            if (_crypto is null) throw new CryptographicException("Encrypted manifest requires a repository key.");
            clear = _crypto.Decrypt(stored, ManifestAad(agentId, backupId));
        }
        else
        {
            clear = stored.ToArray();
        }

        try
        {
            using var document = JsonDocument.Parse(clear);
            var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                ? schemaElement.GetString()
                : null;

            if (string.Equals(schemaVersion, "1", StringComparison.Ordinal))
            {
                if (!allowUpgrade) throw new InvalidDataException("Legacy manifest cannot be upgraded from retention quarantine.");
                var legacy = JsonSerializer.Deserialize<LegacyBackupManifest>(clear, JsonOptions)
                    ?? throw new InvalidDataException("Legacy manifest could not be deserialized.");
                var upgraded = await UpgradeLegacyManifestAsync(legacy, cancellationToken).ConfigureAwait(false);
                await WriteManifestCoreAsync(upgraded, allowRewrite: true, cancellationToken).ConfigureAwait(false);
                DeleteLegacyManifestFiles(path);
                return upgraded;
            }

            if (string.Equals(schemaVersion, "2", StringComparison.Ordinal))
            {
                if (!allowUpgrade) throw new InvalidDataException("Version 2 manifest cannot be upgraded from retention quarantine.");
                var version2 = JsonSerializer.Deserialize<BackupManifest>(clear, JsonOptions)
                    ?? throw new InvalidDataException("Version 2 manifest could not be deserialized.");
                var upgraded = version2 with
                {
                    SchemaVersion = "3",
                    IncrementalMode = "legacy-v2",
                    EncryptionKeyId = _crypto?.KeyId
                };
                await WriteManifestCoreAsync(upgraded, allowRewrite: true, cancellationToken).ConfigureAwait(false);
                DeleteLegacyManifestFiles(path);
                return upgraded;
            }

            if (!string.Equals(schemaVersion, "3", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported manifest schema version: {schemaVersion ?? "<missing>"}.");

            return JsonSerializer.Deserialize<BackupManifest>(clear, JsonOptions)
                ?? throw new InvalidDataException("Manifest could not be deserialized.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task<IReadOnlyList<BackupManifest>> ListManifestsAsync(string agentId, string? sourceRoot, CancellationToken cancellationToken)
    {
        var safeAgent = SafeSegment(agentId);
        var directory = Path.Combine(_root, "manifests", safeAgent);
        if (!Directory.Exists(directory)) return [];
        var ids = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var result = new List<BackupManifest>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(agentId, id!, cancellationToken).ConfigureAwait(false);
            if (sourceRoot is null || PathsEqual(manifest.SourceRoot, sourceRoot)) result.Add(manifest);
        }
        return result;
    }

    public async Task<IReadOnlyList<BackupManifest>> ListAllManifestsAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(_root, "manifests");
        if (!Directory.Exists(root)) return [];
        var result = new List<BackupManifest>();
        foreach (var agentDirectory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agentId = Path.GetFileName(agentDirectory);
            result.AddRange(await ListManifestsAsync(agentId, null, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    public async ValueTask<IAsyncDisposable> AcquireMutationLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _mutationLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                return new RepositoryMutationLease(stream);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task QuarantineManifestAsync(
        BackupManifest manifest,
        DateTimeOffset immutableUntilUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (DateTimeOffset.UtcNow < immutableUntilUtc)
            throw new InvalidOperationException($"Restore point {manifest.BackupId} is immutable until {immutableUntilUtc:O}.");

        var activePath = ResolveManifestPath(manifest.AgentId, manifest.BackupId);
        var active = await ReadManifestCoreAsync(
            manifest.AgentId, manifest.BackupId, activePath, allowUpgrade: true, cancellationToken).ConfigureAwait(false);
        if (active.CreatedAtUtc != manifest.CreatedAtUtc || !string.Equals(active.SourceRoot, manifest.SourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Retention manifest identity changed before quarantine.");

        var agentDirectory = Path.Combine(_quarantineRoot, SafeSegment(manifest.AgentId));
        Directory.CreateDirectory(agentDirectory);
        var finalDirectory = Path.Combine(agentDirectory, SafeSegment(manifest.BackupId));
        if (!Directory.Exists(finalDirectory))
        {
            var stagingDirectory = finalDirectory + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var quarantinedAtUtc = DateTimeOffset.UtcNow;
                foreach (var candidate in ManifestCandidatePaths(manifest.AgentId, manifest.BackupId))
                {
                    if (!File.Exists(candidate)) continue;
                    await CopyFileDurablyAsync(candidate, Path.Combine(stagingDirectory, Path.GetFileName(candidate)), cancellationToken).ConfigureAwait(false);
                    var sidecar = candidate + ".sha256";
                    if (File.Exists(sidecar))
                        await CopyFileDurablyAsync(sidecar, Path.Combine(stagingDirectory, Path.GetFileName(sidecar)), cancellationToken).ConfigureAwait(false);
                }
                var metadata = JsonSerializer.SerializeToUtf8Bytes(
                    new QuarantineMetadata(manifest.AgentId, manifest.BackupId, quarantinedAtUtc), JsonOptions);
                await AtomicWriteAsync(Path.Combine(stagingDirectory, "quarantine.json"), metadata, cancellationToken).ConfigureAwait(false);
                Directory.Move(stagingDirectory, finalDirectory);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
        }

        // The complete recovery copy is durable before active files are removed.
        foreach (var candidate in ManifestCandidatePaths(manifest.AgentId, manifest.BackupId))
        {
            if (File.Exists(candidate)) File.Delete(candidate);
            if (File.Exists(candidate + ".sha256")) File.Delete(candidate + ".sha256");
        }
    }

    public async Task<IReadOnlyList<QuarantinedBackupManifest>> ListQuarantinedManifestsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_quarantineRoot)) return [];
        var result = new List<QuarantinedBackupManifest>();
        foreach (var metadataPath in Directory.EnumerateFiles(_quarantineRoot, "quarantine.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(metadataPath)!;
            if (Path.GetFileName(directory).Contains(".staging-", StringComparison.Ordinal))
                continue;
            var metadata = JsonSerializer.Deserialize<QuarantineMetadata>(
                await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException($"Retention quarantine metadata is invalid: {metadataPath}");
            var manifestPath = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .SingleOrDefault(path =>
                    path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                    (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                     !path.EndsWith("quarantine.json", StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidDataException($"Retention quarantine manifest is missing: {directory}");
            var manifest = await ReadManifestCoreAsync(
                metadata.AgentId, metadata.BackupId, manifestPath, allowUpgrade: false, cancellationToken).ConfigureAwait(false);
            result.Add(new(manifest, metadata.QuarantinedAtUtc));
        }
        return result;
    }

    public async Task PurgeQuarantinedManifestAsync(
        string agentId,
        string backupId,
        DateTimeOffset expectedQuarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_quarantineRoot, SafeSegment(agentId), SafeSegment(backupId));
        var metadataPath = Path.Combine(directory, "quarantine.json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("Retention quarantine metadata was not found.", metadataPath);
        var metadata = JsonSerializer.Deserialize<QuarantineMetadata>(
            await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Retention quarantine metadata is invalid.");
        var purgeBeforeUtc = DateTimeOffset.UtcNow.Subtract(RetentionSafetyDefaults.ManifestQuarantinePeriod);
        if (metadata.QuarantinedAtUtc != expectedQuarantinedAtUtc || metadata.QuarantinedAtUtc > purgeBeforeUtc)
            throw new InvalidOperationException("Retention quarantine grace period has not elapsed or metadata changed.");
        Directory.Delete(directory, recursive: true);
    }

    public async Task<IReadOnlySet<string>> GetReferencedEncryptionKeyIdsAsync(CancellationToken cancellationToken)
    {
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>();
        if (File.Exists(_metadataEncryptedPath)) candidates.Add(_metadataEncryptedPath);
        candidates.AddRange(Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*.chunk", SearchOption.AllDirectories));
        candidates.AddRange(Directory.EnumerateFiles(Path.Combine(_root, "manifests"), "*.manifest", SearchOption.AllDirectories));
        candidates.AddRange(Directory.EnumerateFiles(_quarantineRoot, "*.manifest", SearchOption.AllDirectories));

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = await ReadPrefixAsync(path, 256, cancellationToken).ConfigureAwait(false);
            try
            {
                var keyId = RepositoryCryptoContext.ReadKeyId(prefix);
                if (!string.IsNullOrWhiteSpace(keyId)) keyIds.Add(keyId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(prefix);
            }
        }
        return keyIds;
    }

    public async IAsyncEnumerable<string> EnumerateChunkHashesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = Path.Combine(_root, "chunks");
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.chunk", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var hash = Path.GetFileNameWithoutExtension(path);
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) yield return hash.ToLowerInvariant();
        }
    }

    public Task<bool> DeleteChunkIfOlderThanAsync(string sha256, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetChunkPath(sha256);
        if (!File.Exists(path)) return Task.FromResult(false);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (lastWrite > cutoffUtc.UtcDateTime) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<Stream> OpenChunkReadAsync(string sha256, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetChunkPath(sha256);
        var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!RepositoryCryptoContext.IsEncrypted(stored))
        {
            try { EnsurePlaintextAllowedForLegacyMigration($"chunk {sha256}"); }
            catch
            {
                CryptographicOperations.ZeroMemory(stored);
                throw;
            }
            return new ZeroingMemoryStream(stored);
        }
        if (_crypto is null)
        {
            CryptographicOperations.ZeroMemory(stored);
            throw new CryptographicException("Encrypted chunk requires a repository key.");
        }
        var clear = _crypto.Decrypt(stored, ChunkAad(sha256));
        CryptographicOperations.ZeroMemory(stored);
        return new ZeroingMemoryStream(clear);
    }

    public string DescribeManifestLocation(string agentId, string backupId) => GetManifestPath(agentId, backupId);

    public async Task<ScrubStatistics> ScrubAsync(bool migrateLegacyPlaintext, CancellationToken cancellationToken)
    {
        var verifiedChunks = 0;
        var migratedChunks = 0;
        long verifiedStoredPlaintextBytes = 0;
        var chunkLengths = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var hash in EnumerateChunkHashesAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = GetChunkPath(hash);
            var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var wasPlaintext = !RepositoryCryptoContext.IsEncrypted(stored);
            var storedKeyId = wasPlaintext ? null : RepositoryCryptoContext.ReadKeyId(stored);
            byte[] clear;
            if (wasPlaintext)
            {
                EnsurePlaintextAllowedForLegacyMigration($"chunk {hash}");
                clear = stored.ToArray();
            }
            else
            {
                if (_crypto is null) throw new CryptographicException("Repository scrub requires the encryption key ring.");
                clear = _crypto.Decrypt(stored, ChunkAad(hash));
            }
            try
            {
                var actual = Convert.ToHexString(SHA256.HashData(clear)).ToLowerInvariant();
                if (!string.Equals(actual, hash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Repository scrub failed for chunk {hash}.");
                verifiedStoredPlaintextBytes += clear.Length;
                chunkLengths[hash] = clear.Length;
                verifiedChunks++;
                var requiresRewrite = _crypto is not null && (wasPlaintext || !string.Equals(storedKeyId, _crypto.KeyId, StringComparison.Ordinal));
                if (requiresRewrite && migrateLegacyPlaintext)
                {
                    await WriteChunkAtomicallyAsync(path, hash, clear, cancellationToken).ConfigureAwait(false);
                    migratedChunks++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        var manifests = await ListAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in manifest.Files)
            {
                using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long reconstructedLength = 0;
                foreach (var chunk in file.Chunks)
                {
                    if (!chunkLengths.TryGetValue(chunk.Sha256, out var storedLength))
                        throw new InvalidDataException($"Manifest {manifest.BackupId} references missing chunk {chunk.Sha256}.");
                    if (storedLength != chunk.Length)
                        throw new InvalidDataException($"Manifest {manifest.BackupId} chunk length mismatch for {chunk.Sha256}.");
                    await using var input = await OpenChunkReadAsync(chunk.Sha256, cancellationToken).ConfigureAwait(false);
                    var buffer = GC.AllocateUninitializedArray<byte>(Math.Min(1024 * 1024, Math.Max(1, chunk.Length)));
                    var remaining = chunk.Length;
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException($"Chunk {chunk.Sha256} ended early during scrub.");
                        fileHash.AppendData(buffer, 0, read);
                        reconstructedLength += read;
                        remaining -= read;
                    }
                    if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException($"Chunk {chunk.Sha256} has trailing bytes during scrub.");
                }
                if (reconstructedLength != file.Length)
                    throw new InvalidDataException($"Manifest {manifest.BackupId} file length mismatch for {file.RelativePath}.");
                var actualFileHash = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualFileHash, file.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Manifest {manifest.BackupId} file integrity failure for {file.RelativePath}.");
            }

            if (_crypto is not null && migrateLegacyPlaintext)
                await WriteManifestCoreAsync(manifest with { SchemaVersion = "3", EncryptionKeyId = _crypto.KeyId }, allowRewrite: true, cancellationToken).ConfigureAwait(false);
        }

        if (_crypto is not null && migrateLegacyPlaintext)
        {
            _metadata = _metadata with { MigrationState = "complete", EncryptionKeyId = _crypto.KeyId, Encrypted = true };
            WriteMetadata(_metadata);
        }
        return new ScrubStatistics(verifiedChunks, migratedChunks, manifests.Count, verifiedStoredPlaintextBytes);
    }

    private RepositoryMetadata InitializeMetadata()
    {
        if (File.Exists(_metadataEncryptedPath))
        {
            if (_crypto is null) throw new CryptographicException("Encrypted repository metadata requires the repository key ring.");
            var envelope = File.ReadAllBytes(_metadataEncryptedPath);
            byte[] clear;
            try { clear = _crypto.Decrypt(envelope, MetadataAad(_repositoryId)); }
            finally { CryptographicOperations.ZeroMemory(envelope); }
            try
            {
                var metadata = JsonSerializer.Deserialize<RepositoryMetadata>(clear, JsonOptions)
                    ?? throw new InvalidDataException("Repository metadata is invalid.");
                ValidateMetadataIdentity(metadata);
                if (!metadata.Encrypted) throw new InvalidDataException("Authenticated repository metadata unexpectedly marks the repository as plaintext.");
                if (!_crypto.HasKey(metadata.EncryptionKeyId))
                    throw new CryptographicException($"Repository requires key id {metadata.EncryptionKeyId ?? "<missing>"}, which is not in the Agent key ring.");
                if (!string.Equals(metadata.EncryptionKeyId, _crypto.KeyId, StringComparison.Ordinal))
                {
                    metadata = metadata with { EncryptionKeyId = _crypto.KeyId, MigrationState = "rekeying" };
                    WriteMetadata(metadata);
                }
                if (File.Exists(_metadataJsonPath)) File.Delete(_metadataJsonPath);
                return metadata;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        if (File.Exists(_metadataJsonPath))
        {
            var metadata = JsonSerializer.Deserialize<RepositoryMetadata>(File.ReadAllText(_metadataJsonPath), JsonOptions)
                ?? throw new InvalidDataException("Repository metadata is invalid.");
            ValidateMetadataIdentity(metadata);
            if (metadata.Encrypted)
                throw new InvalidDataException("Encrypted repository metadata must use authenticated repository.meta format.");
            if (_crypto is not null)
            {
                if (!_allowLegacyInitialization)
                    throw new InvalidOperationException("Plaintext repository requires an explicit scrub/migration before encrypted use.");
                metadata = metadata with { Encrypted = true, EncryptionKeyId = _crypto.KeyId, MigrationState = "mixed" };
                WriteMetadata(metadata);
            }
            return metadata;
        }

        var hasLegacyChunks = Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*.chunk", SearchOption.AllDirectories).Any();
        var hasLegacyManifests = Directory.EnumerateFiles(Path.Combine(_root, "manifests"), "*.json", SearchOption.AllDirectories).Any();
        var hasV3ManifestFiles = Directory.EnumerateFiles(Path.Combine(_root, "manifests"), "*.manifest", SearchOption.AllDirectories).Any();
        var hasEncryptedChunkEvidence = HasEncryptedChunkEvidence();
        if (hasV3ManifestFiles || hasEncryptedChunkEvidence)
            throw new InvalidDataException("Repository metadata is missing while v3/encrypted data exists. Refusing downgrade-style reinitialization.");

        var hasLegacyData = hasLegacyChunks || hasLegacyManifests;
        if (_crypto is not null && hasLegacyData && !_allowLegacyInitialization)
            throw new InvalidOperationException("Legacy repository detected. Run an explicit repository scrub/migration before backup or restore.");

        var created = new RepositoryMetadata(
            "3",
            _repositoryId,
            _crypto is not null,
            _crypto?.KeyId,
            _crypto is not null && hasLegacyData ? "mixed" : "complete",
            DateTimeOffset.UtcNow);
        WriteMetadata(created);
        return created;
    }

    private bool HasEncryptedChunkEvidence()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*.chunk", SearchOption.AllDirectories).Take(32))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256, FileOptions.SequentialScan);
            var prefix = new byte[Math.Min(256, checked((int)Math.Min(stream.Length, 256)))];
            var read = stream.Read(prefix, 0, prefix.Length);
            if (RepositoryCryptoContext.IsEncrypted(prefix.AsSpan(0, read))) return true;
        }
        return false;
    }

    private void ValidateMetadataIdentity(RepositoryMetadata metadata)
    {
        if (!string.Equals(metadata.SchemaVersion, "3", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported repository metadata schema: {metadata.SchemaVersion}.");
        if (!string.Equals(metadata.RepositoryId, _repositoryId, StringComparison.Ordinal))
            throw new InvalidDataException($"Repository id mismatch. Expected {_repositoryId}, found {metadata.RepositoryId}.");
    }

    private void WriteMetadata(RepositoryMetadata metadata)
    {
        var clear = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, JsonOptions));
        try
        {
            if (metadata.Encrypted)
            {
                if (_crypto is null) throw new CryptographicException("Cannot write encrypted repository metadata without a key ring.");
                var envelope = _crypto.Encrypt(clear, MetadataAad(_repositoryId));
                try
                {
                    AtomicWriteSync(_metadataEncryptedPath, envelope);
                    if (File.Exists(_metadataJsonPath)) File.Delete(_metadataJsonPath);
                }
                finally { CryptographicOperations.ZeroMemory(envelope); }
            }
            else
            {
                AtomicWriteSync(_metadataJsonPath, clear);
                if (File.Exists(_metadataEncryptedPath)) File.Delete(_metadataEncryptedPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void AtomicWriteSync(string path, ReadOnlySpan<byte> content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task WriteChunkAtomicallyAsync(string path, string sha256, ReadOnlyMemory<byte> clear, CancellationToken cancellationToken)
    {
        byte[] stored = _crypto is null ? clear.ToArray() : _crypto.Encrypt(clear.Span, ChunkAad(sha256));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteThroughAsync(temp, stored, throttle: true, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<BackupManifest> UpgradeLegacyManifestAsync(LegacyBackupManifest legacy, CancellationToken cancellationToken)
    {
        var files = new List<BackupFileEntry>(legacy.Files.Count);
        foreach (var file in legacy.Files)
        {
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long reconstructedLength = 0;
            foreach (var chunk in file.Chunks)
            {
                ValidateSha256(chunk.Sha256);
                await using var input = await OpenChunkReadAsync(chunk.Sha256, cancellationToken).ConfigureAwait(false);
                using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
                var remaining = chunk.Length;
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException($"Legacy chunk {chunk.Sha256} ended early.");
                    chunkHash.AppendData(buffer, 0, read);
                    fileHash.AppendData(buffer, 0, read);
                    remaining -= read;
                    reconstructedLength += read;
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException($"Legacy chunk {chunk.Sha256} has unexpected trailing data.");
                var actualChunkHash = Convert.ToHexString(chunkHash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualChunkHash, chunk.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Legacy chunk integrity failure: {chunk.Sha256}.");
            }

            if (reconstructedLength != file.Length)
                throw new InvalidDataException($"Legacy file length mismatch: {file.RelativePath}.");
            files.Add(new BackupFileEntry(
                file.RelativePath,
                file.Length,
                file.LastWriteTimeUtc,
                Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant(),
                file.Chunks));
        }

        return new BackupManifest(
            "3",
            legacy.BackupId,
            legacy.AgentId,
            legacy.SourceRoot,
            legacy.CreatedAtUtc,
            files,
            legacy.LogicalBytes,
            legacy.UploadedBytes,
            legacy.NewChunks,
            legacy.ReusedChunks,
            "fixed-v1:4194304",
            SnapshotBacked: false,
            ParentBackupId: null,
            IncrementalMode: "legacy-v1",
            ReusedFiles: 0,
            EncryptionKeyId: _crypto?.KeyId);
    }

    private async Task AtomicWriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteThroughAsync(temp, content, throttle: false, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task CopyFileDurablyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WriteThroughAsync(string path, ReadOnlyMemory<byte> content, bool throttle, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        const int blockSize = 64 * 1024;
        var offset = 0;
        while (offset < content.Length)
        {
            var count = Math.Min(blockSize, content.Length - offset);
            if (throttle) await _limiter.WaitAsync(count, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content.Slice(offset, count), cancellationToken).ConfigureAwait(false);
            if (throttle) _transferObserver?.OnBytesWritten(count);
            offset += count;
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadPrefixAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[Math.Min(maxBytes, checked((int)Math.Min(stream.Length, maxBytes)))];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private string GetChunkPath(string sha256)
    {
        ValidateSha256(sha256);
        return Path.Combine(_root, "chunks", sha256[..2], sha256[2..4], sha256 + ".chunk");
    }

    private string GetManifestPath(string agentId, string backupId) =>
        Path.Combine(_root, "manifests", SafeSegment(agentId), SafeSegment(backupId) + ".manifest");

    private IEnumerable<string> ManifestCandidatePaths(string agentId, string backupId)
    {
        var basePath = Path.Combine(_root, "manifests", SafeSegment(agentId), SafeSegment(backupId));
        yield return basePath + ".manifest";
        yield return basePath + ".json";
    }

    private string ResolveManifestPath(string agentId, string backupId)
    {
        foreach (var path in ManifestCandidatePaths(agentId, backupId))
            if (File.Exists(path)) return path;
        throw new FileNotFoundException("Backup manifest was not found.", GetManifestPath(agentId, backupId));
    }

    private static void DeleteLegacyManifestFiles(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".sha256")) File.Delete(path + ".sha256");
    }

    private void EnsurePlaintextAllowedForLegacyMigration(string objectDescription)
    {
        if (!_metadata.Encrypted) return;
        if (_allowLegacyInitialization && !string.Equals(_metadata.MigrationState, "complete", StringComparison.Ordinal)) return;
        throw new InvalidDataException($"Plaintext {objectDescription} detected in encrypted repository outside an explicit migration operation.");
    }

    private static string ChunkAad(string sha256) => "chunk|" + sha256;
    private static string MetadataAad(string repositoryId) => "repository-metadata|" + repositoryId;
    private static string ManifestAad(string agentId, string backupId) => $"manifest|{agentId}|{backupId}";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invalid SHA-256 value.", nameof(value));
    }

    private static void ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Identifier contains unsupported characters.", paramName);
    }

    private static string SafeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Unsafe path segment.", nameof(value));
        return value;
    }

    private sealed class ZeroingMemoryStream : MemoryStream
    {
        private readonly byte[] _buffer;
        public ZeroingMemoryStream(byte[] buffer) : base(buffer, writable: false) => _buffer = buffer;
        protected override void Dispose(bool disposing)
        {
            if (disposing) CryptographicOperations.ZeroMemory(_buffer);
            base.Dispose(disposing);
        }
    }

    private sealed class RepositoryMutationLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private sealed record QuarantineMetadata(string AgentId, string BackupId, DateTimeOffset QuarantinedAtUtc);

    private sealed record RepositoryMetadata(string SchemaVersion, string RepositoryId, bool Encrypted, string? EncryptionKeyId, string MigrationState, DateTimeOffset CreatedAtUtc);

    private sealed record LegacyBackupManifest(
        string SchemaVersion,
        string BackupId,
        string AgentId,
        string SourceRoot,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<LegacyBackupFileEntry> Files,
        long LogicalBytes,
        long UploadedBytes,
        int NewChunks,
        int ReusedChunks);

    private sealed record LegacyBackupFileEntry(
        string RelativePath,
        long Length,
        DateTimeOffset LastWriteTimeUtc,
        IReadOnlyList<BackupChunkRef> Chunks);
}

public sealed record ScrubStatistics(int VerifiedChunks, int MigratedChunks, int VerifiedManifests, long VerifiedLogicalBytes);
