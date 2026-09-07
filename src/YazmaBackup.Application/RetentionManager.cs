using System.Globalization;
using YazmaBackup.Domain;

namespace YazmaBackup.Application;

public sealed record RetentionSummary(int DeletedRestorePoints, int DeletedChunks, int KeptRestorePoints);

public sealed class RetentionManager(IBackupRepository repository)
{
    private static readonly TimeSpan OrphanChunkGracePeriod = TimeSpan.FromHours(24);

    public async Task<RetentionSummary> ApplyAsync(
        string agentId,
        string sourceRoot,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        policy.Validate();
        var manifests = (await repository.ListManifestsAsync(agentId, Path.GetFullPath(sourceRoot), cancellationToken).ConfigureAwait(false))
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToArray();
        if (manifests.Length == 0) return new RetentionSummary(0, 0, 0);

        var keep = SelectKeepSet(manifests, policy);
        var delete = manifests.Where(m => !keep.Contains(m.BackupId)).ToArray();
        foreach (var manifest in delete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await repository.DeleteManifestAsync(manifest.AgentId, manifest.BackupId, cancellationToken).ConfigureAwait(false);
        }

        var allRemaining = await repository.ListAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var referenced = new HashSet<string>(
            allRemaining.SelectMany(m => m.Files).SelectMany(f => f.Chunks).Select(c => c.Sha256),
            StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow.Subtract(OrphanChunkGracePeriod);
        var deletedChunks = 0;
        await foreach (var hash in repository.EnumerateChunkHashesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (referenced.Contains(hash)) continue;
            if (await repository.DeleteChunkIfOlderThanAsync(hash, cutoff, cancellationToken).ConfigureAwait(false))
                deletedChunks++;
        }

        return new RetentionSummary(delete.Length, deletedChunks, keep.Count);
    }

    internal static HashSet<string> SelectKeepSet(IReadOnlyList<BackupManifest> manifests, RetentionPolicy policy)
    {
        var ordered = manifests.OrderByDescending(m => m.CreatedAtUtc).ToArray();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ordered.Take(policy.KeepLast)) keep.Add(item.BackupId);

        if (policy.ImmutabilityHours > 0)
        {
            var immutableCutoff = DateTimeOffset.UtcNow.AddHours(-policy.ImmutabilityHours);
            foreach (var item in ordered.Where(m => m.CreatedAtUtc >= immutableCutoff))
                keep.Add(item.BackupId);
        }

        foreach (var item in ordered
                     .GroupBy(m => DateOnly.FromDateTime(m.CreatedAtUtc.UtcDateTime))
                     .OrderByDescending(g => g.Key)
                     .Take(policy.KeepDaily)
                     .Select(g => g.OrderByDescending(m => m.CreatedAtUtc).First()))
            keep.Add(item.BackupId);

        foreach (var item in ordered
                     .GroupBy(m => (ISOWeek.GetYear(m.CreatedAtUtc.UtcDateTime), ISOWeek.GetWeekOfYear(m.CreatedAtUtc.UtcDateTime)))
                     .OrderByDescending(g => g.Key.Item1).ThenByDescending(g => g.Key.Item2)
                     .Take(policy.KeepWeekly)
                     .Select(g => g.OrderByDescending(m => m.CreatedAtUtc).First()))
            keep.Add(item.BackupId);

        foreach (var item in ordered
                     .GroupBy(m => (m.CreatedAtUtc.UtcDateTime.Year, m.CreatedAtUtc.UtcDateTime.Month))
                     .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
                     .Take(policy.KeepMonthly)
                     .Select(g => g.OrderByDescending(m => m.CreatedAtUtc).First()))
            keep.Add(item.BackupId);

        return keep;
    }
}
