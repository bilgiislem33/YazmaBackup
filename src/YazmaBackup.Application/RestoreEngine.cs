using System.Security.Cryptography;
using YazmaBackup.Domain;

namespace YazmaBackup.Application;

public sealed record RestoreSummary(int RestoredFiles, long RestoredBytes, int AlreadyPresentFiles);

public sealed class RestoreEngine(IBackupRepository repository)
{
    public async Task<RestoreSummary> RestoreAsync(string agentId, string backupId, string destinationRoot, bool overwriteExisting, CancellationToken cancellationToken)
    {
        var manifest = await repository.ReadManifestAsync(agentId, backupId, cancellationToken).ConfigureAwait(false);
        return await RestoreManifestAsync(manifest, destinationRoot, overwriteExisting, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RestoreSummary> RestoreSelectedAsync(
        string agentId,
        string backupId,
        string destinationRoot,
        IReadOnlyList<string> includePaths,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        if (includePaths is null || includePaths.Count == 0) throw new ArgumentException("At least one include path is required.", nameof(includePaths));
        var normalized = includePaths.Select(NormalizeRelativeSelector).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var manifest = await repository.ReadManifestAsync(agentId, backupId, cancellationToken).ConfigureAwait(false);
        var selected = manifest.Files.Where(f => normalized.Any(selector => MatchesSelector(f.RelativePath, selector))).ToArray();
        if (selected.Length == 0) throw new KeyNotFoundException("Selected restore paths do not match any files in the restore point.");
        var filtered = manifest with { Files = selected };
        return await RestoreManifestAsync(filtered, destinationRoot, overwriteExisting, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RestoreSummary> RestoreSandboxAsync(
        string agentId,
        string backupId,
        string sandboxRoot,
        int maxFiles,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxFiles is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (maxBytes is < 1 or > 5L * 1024 * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var manifest = await repository.ReadManifestAsync(agentId, backupId, cancellationToken).ConfigureAwait(false);
        if (manifest.Files.Count > maxFiles) throw new InvalidOperationException($"Restore sandbox file limit exceeded: {manifest.Files.Count} > {maxFiles}.");
        if (manifest.LogicalBytes > maxBytes) throw new InvalidOperationException($"Restore sandbox byte limit exceeded: {manifest.LogicalBytes} > {maxBytes}.");
        return await RestoreManifestAsync(manifest, sandboxRoot, overwriteExisting: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreSummary> RestoreManifestAsync(BackupManifest manifest, string destinationRoot, bool overwriteExisting, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var restoredFiles = 0;
        var alreadyPresentFiles = 0;
        long restoredBytes = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(root, file.RelativePath));
            EnsureUnderRoot(root, destination, file.RelativePath);

            if (!overwriteExisting && File.Exists(destination))
            {
                if (await FileMatchesAsync(destination, file, cancellationToken).ConfigureAwait(false))
                {
                    alreadyPresentFiles++;
                    restoredBytes += file.Length;
                    continue;
                }
                throw new IOException($"Restore target already exists with different content: {destination}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".yazmabackup-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    foreach (var chunk in file.Chunks)
                        await CopyVerifiedChunkAsync(chunk, output, fileHash, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var actualLength = new FileInfo(temp).Length;
                if (actualLength != file.Length)
                    throw new InvalidDataException($"Restored length mismatch for {file.RelativePath}. Expected {file.Length}, got {actualLength}.");

                var restoredHash = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(restoredHash, file.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Restored file integrity failure: {file.RelativePath}.");

                File.Move(temp, destination, overwriteExisting);
                File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc.UtcDateTime);
                restoredFiles++;
                restoredBytes += file.Length;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        return new RestoreSummary(restoredFiles, restoredBytes, alreadyPresentFiles);
    }

    private async Task CopyVerifiedChunkAsync(BackupChunkRef chunk, Stream output, IncrementalHash fileHash, CancellationToken cancellationToken)
    {
        await using var input = await repository.OpenChunkReadAsync(chunk.Sha256, cancellationToken).ConfigureAwait(false);
        using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        var remaining = chunk.Length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"Chunk {chunk.Sha256} ended early.");
            chunkHash.AppendData(buffer, 0, read);
            fileHash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException($"Chunk {chunk.Sha256} has unexpected trailing data.");
        var actual = Convert.ToHexString(chunkHash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, chunk.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Chunk integrity failure: {chunk.Sha256}.");
    }

    private static async Task<bool> FileMatchesAsync(string path, BackupFileEntry entry, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != entry.Length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return string.Equals(actual, entry.Sha256, StringComparison.Ordinal);
    }

    private static string NormalizeRelativeSelector(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Restore selector is invalid.", nameof(value));
        return normalized;
    }

    private static bool MatchesSelector(string relativePath, string selector)
    {
        var candidate = relativePath.Replace('\\', '/').TrimStart('/');
        return string.Equals(candidate, selector, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(selector + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureUnderRoot(string root, string destination, string relativePath)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && !string.Equals(destination, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest path escapes restore root: {relativePath}");
    }
}
