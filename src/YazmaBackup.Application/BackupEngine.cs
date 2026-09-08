using System.Security.Cryptography;
using YazmaBackup.Domain;

namespace YazmaBackup.Application;

public sealed class BackupEngine(
    IChunker chunker,
    IBackupRepository repository,
    ISnapshotProvider snapshotProvider,
    IIncrementalChangeTracker? changeTracker = null,
    IBackupProtectionGuard? protectionGuard = null,
    IBackupProgressObserver? progressObserver = null)
{
    public Task<BackupManifest> BackupDirectoryAsync(
        string agentId,
        string sourceRoot,
        CancellationToken cancellationToken) =>
        BackupDirectoryAsync(agentId, sourceRoot, new ProtectionPolicy(false), cancellationToken);

    public async Task<BackupManifest> BackupDirectoryAsync(
        string agentId,
        string sourceRoot,
        ProtectionPolicy protectionPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);

        await using var mutationLease = repository is IRepositoryMutationCoordinator coordinator
            ? await coordinator.AcquireMutationLeaseAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var normalizedSource = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(normalizedSource))
            throw new DirectoryNotFoundException(normalizedSource);

        var previous = (await repository.ListManifestsAsync(agentId, normalizedSource, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(m => m.CreatedAtUtc)
            .FirstOrDefault();

        // Capture the USN boundary before taking the snapshot. If a file changes after this
        // boundary, the next backup will still observe that journal record even if this
        // snapshot predates the change. The checkpoint is committed only after the manifest.
        var changeSet = changeTracker is null
            ? FullScanChangeSet("full-scan:no-tracker")
            : await changeTracker.CaptureAsync(normalizedSource, cancellationToken).ConfigureAwait(false);

        await using var snapshot = await snapshotProvider.CreateAsync(normalizedSource, cancellationToken).ConfigureAwait(false);
        var readableRoot = Path.GetFullPath(snapshot.ReadablePath);

        protectionPolicy.Validate();
        if (protectionGuard is not null && protectionPolicy.Enabled)
        {
            var assessment = await protectionGuard.AssessAsync(readableRoot, previous, changeSet, protectionPolicy, cancellationToken).ConfigureAwait(false);
            if (assessment.Suspicious)
                throw new RansomwareSuspectedException(assessment);
        }

        var previousFiles = previous?.Files.ToDictionary(f => NormalizeRelative(f.RelativePath), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, BackupFileEntry>(StringComparer.OrdinalIgnoreCase);
        var changed = changeSet.ChangedRelativePaths.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(changeSet.ChangedRelativePaths.Select(NormalizeRelative), StringComparer.OrdinalIgnoreCase);

        progressObserver?.Report("scanning", 0, 0, 0, 0);
        var sourceFiles = EnumerateSafeFiles(readableRoot).ToArray();
        long logicalBytesTotal = 0;
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { logicalBytesTotal = checked(logicalBytesTotal + new FileInfo(sourceFile).Length); }
            catch (OverflowException) { logicalBytesTotal = long.MaxValue; }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        progressObserver?.Report("processing", 0, sourceFiles.Length, 0, logicalBytesTotal);

        var files = new List<BackupFileEntry>();
        long logicalBytes = 0;
        long uploadedBytes = 0;
        var newChunks = 0;
        var reusedChunks = 0;
        var reusedFiles = 0;

        var filesProcessed = 0;
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var relative = NormalizeRelative(Path.GetRelativePath(readableRoot, file));

            if (changeSet.CanReuseUnchangedFiles &&
                !changed.Contains(relative) &&
                previousFiles.TryGetValue(relative, out var prior) &&
                prior.Length == info.Length &&
                prior.LastWriteTimeUtc.UtcDateTime == info.LastWriteTimeUtc)
            {
                files.Add(prior);
                logicalBytes += prior.Length;
                reusedChunks += prior.Chunks.Count;
                reusedFiles++;
                filesProcessed++;
                progressObserver?.Report("processing", filesProcessed, sourceFiles.Length, logicalBytes, logicalBytesTotal);
                continue;
            }

            var chunkRefs = new List<BackupChunkRef>();
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await foreach (var chunk in chunker.ReadChunksAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                fileHash.AppendData(chunk.Span);
                var hash = Convert.ToHexString(SHA256.HashData(chunk.Span)).ToLowerInvariant();
                var exists = await repository.ContainsChunkAsync(hash, cancellationToken).ConfigureAwait(false);
                if (!exists)
                {
                    await repository.PutChunkAsync(hash, chunk, cancellationToken).ConfigureAwait(false);
                    uploadedBytes += chunk.Length;
                    newChunks++;
                }
                else
                {
                    reusedChunks++;
                }

                chunkRefs.Add(new BackupChunkRef(hash, chunk.Length));
            }

            logicalBytes += info.Length;
            files.Add(new BackupFileEntry(
                relative,
                info.Length,
                info.LastWriteTimeUtc,
                Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant(),
                chunkRefs));
            filesProcessed++;
            progressObserver?.Report("processing", filesProcessed, sourceFiles.Length, logicalBytes, logicalBytesTotal);
        }

        progressObserver?.Report("committing", filesProcessed, sourceFiles.Length, logicalBytes, logicalBytesTotal);
        var backupId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var manifest = new BackupManifest(
            "3",
            backupId,
            agentId,
            normalizedSource,
            DateTimeOffset.UtcNow,
            files,
            logicalBytes,
            uploadedBytes,
            newChunks,
            reusedChunks,
            chunker.ChunkerId,
            snapshot.SnapshotBacked,
            previous?.BackupId,
            changeSet.Mode,
            reusedFiles,
            repository.EncryptionKeyId);

        await repository.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        await changeSet.CommitAsync(cancellationToken).ConfigureAwait(false);
        progressObserver?.Report("completed", filesProcessed, sourceFiles.Length, logicalBytes, logicalBytesTotal);
        return manifest;
    }

    private static IncrementalChangeSet FullScanChangeSet(string mode) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), mode, _ => Task.CompletedTask);

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        // R5.11.3: Windows user profiles contain legacy junction/reparse-point folders
        // (for example "Belgelerim"/"My Documents") that intentionally deny traversal.
        // Treat them as filesystem boundaries, not backup failures.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(root, "*", options);
    }
}
