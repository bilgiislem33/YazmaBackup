using System.Security.Cryptography;
using YazmaBackup.Application;
using YazmaBackup.Domain;

namespace YazmaBackup.Infrastructure;

public sealed class RansomwareProtectionGuard : IBackupProtectionGuard
{
    private const int MaxEntropySamples = 64;
    private const int SampleBytesPerRegion = 32 * 1024;
    private static readonly HashSet<string> NaturallyCompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".docx", ".epub", ".gif", ".gz", ".heic", ".jpeg", ".jpg", ".m4a", ".mkv", ".mov",
        ".mp3", ".mp4", ".ogg", ".pdf", ".png", ".pptx", ".rar", ".webm", ".webp", ".xlsx", ".zip"
    };

    public async Task<ProtectionAssessment> AssessAsync(
        string readableRoot,
        BackupManifest? previous,
        IncrementalChangeSet changeSet,
        ProtectionPolicy policy,
        CancellationToken cancellationToken)
    {
        policy.Validate();
        if (!policy.Enabled || previous is null || previous.Files.Count == 0)
            return Healthy(previous?.Files.Count ?? 0, CountFiles(readableRoot));

        var previousByPath = previous.Files.ToDictionary(
            x => Normalize(x.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var current = EnumerateFiles(readableRoot)
            .Select(path => new CurrentFile(path, Normalize(Path.GetRelativePath(readableRoot, path)), new FileInfo(path)))
            .ToArray();
        var currentByPath = current.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (changeSet.CanReuseUnchangedFiles)
        {
            foreach (var path in changeSet.ChangedRelativePaths) changed.Add(Normalize(path));
            foreach (var prior in previousByPath.Keys)
                if (!currentByPath.ContainsKey(prior)) changed.Add(prior);
        }
        else
        {
            // When USN reuse is unavailable this is deliberately a content-safe full scan.
            // Size/timestamp alone is insufficient because malware can preserve metadata.
            foreach (var file in current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!previousByPath.TryGetValue(file.RelativePath, out var prior)
                    || prior.Length != file.Info.Length
                    || prior.LastWriteTimeUtc.UtcDateTime != file.Info.LastWriteTimeUtc)
                {
                    changed.Add(file.RelativePath);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(prior.Sha256))
                {
                    changed.Add(file.RelativePath);
                    continue;
                }

                var currentSha256 = await ComputeSha256Async(file.FullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentSha256, prior.Sha256, StringComparison.OrdinalIgnoreCase))
                    changed.Add(file.RelativePath);
            }
            foreach (var prior in previousByPath.Keys)
                if (!currentByPath.ContainsKey(prior)) changed.Add(prior);
        }

        var extensionChanges = 0;
        foreach (var file in current)
        {
            if (previousByPath.ContainsKey(file.RelativePath)) continue;
            var stripped = StripAddedExtension(file.RelativePath);
            if (stripped is not null && previousByPath.ContainsKey(stripped)) extensionChanges++;
        }

        var changedExisting = current.Where(x => changed.Contains(x.RelativePath)).ToArray();
        var entropyCandidates = changedExisting
            .Where(x => x.Info.Length >= 4096 && !NaturallyCompressedExtensions.Contains(Path.GetExtension(x.RelativePath)))
            .OrderByDescending(x => x.Info.Length)
            .Take(MaxEntropySamples)
            .ToArray();

        var highEntropy = 0;
        var sampled = 0;
        foreach (var file in entropyCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entropy = await EstimateEntropyAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            sampled++;
            if (entropy >= policy.EntropyThresholdBitsPerByte) highEntropy++;
        }

        var denominator = Math.Max(1, previousByPath.Count);
        var changedRatio = Math.Min(1.0, (double)changed.Count / denominator);
        var extensionRatio = changed.Count == 0 ? 0 : (double)extensionChanges / changed.Count;
        var entropyRatio = sampled == 0 ? 0 : (double)highEntropy / sampled;

        var extensionAttack = changed.Count >= policy.MinimumChangedFiles
            && extensionChanges >= policy.MinimumExtensionChanges
            && changedRatio >= policy.ChangedFileRatioThreshold
            && extensionRatio >= policy.ExtensionChangeRatioThreshold
            && sampled >= policy.MinimumEntropySamples
            && entropyRatio >= policy.HighEntropyRatioThreshold;

        var massRewriteAttack = changed.Count >= Math.Max(500, policy.MinimumChangedFiles)
            && changedRatio >= Math.Max(0.80, policy.ChangedFileRatioThreshold)
            && sampled >= policy.MinimumEntropySamples
            && entropyRatio >= Math.Max(0.50, policy.HighEntropyRatioThreshold);

        var suspicious = extensionAttack || massRewriteAttack;
        var reason = suspicious
            ? extensionAttack
                ? "mass-extension-change-with-high-entropy"
                : "mass-rewrite-with-high-entropy"
            : "within-policy-thresholds";

        return new ProtectionAssessment(
            suspicious,
            reason,
            previousByPath.Count,
            current.Length,
            changed.Count,
            extensionChanges,
            sampled,
            highEntropy,
            changedRatio,
            extensionRatio,
            entropyRatio);
    }

    private static ProtectionAssessment Healthy(int previous, int current) =>
        new(false, "baseline-or-disabled", previous, current, 0, 0, 0, 0, 0, 0, 0);

    private static int CountFiles(string root) => EnumerateFiles(root).Count();

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        // Keep ransomware preflight traversal aligned with BackupEngine.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(root, "*", options);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<double> EstimateEntropyAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var counts = new long[256];
        long total = 0;
        foreach (var offset in SampleOffsets(info.Length))
        {
            stream.Position = offset;
            var buffer = new byte[Math.Min(SampleBytesPerRegion, (int)Math.Min(int.MaxValue, info.Length - offset))];
            if (buffer.Length == 0) continue;
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < read; i++) counts[buffer[i]]++;
            total += read;
        }
        if (total == 0) return 0;
        double entropy = 0;
        foreach (var count in counts)
        {
            if (count == 0) continue;
            var p = (double)count / total;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private static IEnumerable<long> SampleOffsets(long length)
    {
        yield return 0;
        if (length > SampleBytesPerRegion * 2L)
            yield return Math.Max(0, (length / 2) - (SampleBytesPerRegion / 2));
        if (length > SampleBytesPerRegion)
            yield return Math.Max(0, length - SampleBytesPerRegion);
    }

    private static string? StripAddedExtension(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        if (string.IsNullOrEmpty(ext)) return null;
        return relativePath[..^ext.Length];
    }

    private static string Normalize(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private sealed record CurrentFile(string FullPath, string RelativePath, FileInfo Info);
}
