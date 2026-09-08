using System.Globalization;
using YazmaBackup.Domain;

namespace YazmaBackup.Application;

public sealed record RetentionSummary(int DeletedRestorePoints, int DeletedChunks, int KeptRestorePoints);

public sealed record RetentionPlan(
    string AgentId,
    string SourceRoot,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> InventoryBackupIds,
    IReadOnlyList<string> KeepBackupIds,
    IReadOnlyList<string> DeleteBackupIds);

public sealed class RetentionManager(IBackupRepository repository)
{
    private static readonly TimeSpan OrphanChunkGracePeriod = TimeSpan.FromHours(24);

    public async Task<RetentionSummary> ApplyAsync(
        string agentId,
        string sourceRoot,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(agentId, sourceRoot, policy, cancellationToken).ConfigureAwait(false);
        if (plan.InventoryBackupIds.Count == 0) return new RetentionSummary(0, 0, 0);

        // Destructive work starts only after a complete global inventory can be read and the
        // scoped inventory is proven unchanged. A concurrent backup or an incomplete repository
        // listing therefore aborts retention before the first manifest is removed.
        var allBeforeDelete = await repository.ListAllManifestsAsync(cancellationToken).ConfigureAwait(false);
        var current = (await repository.ListManifestsAsync(agentId, plan.SourceRoot, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToArray();
        var currentIds = current.Select(m => m.BackupId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var plannedIds = plan.InventoryBackupIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!currentIds.SequenceEqual(plannedIds, StringComparer.Ordinal))
            throw new InvalidOperationException("Retention inventory changed after planning; no restore point was deleted.");

        var globalIds = allBeforeDelete
            .Select(m => (m.AgentId, m.BackupId))
            .ToHashSet();
        if (current.Any(m => !globalIds.Contains((m.AgentId, m.BackupId))))
            throw new InvalidDataException("Global repository inventory is incomplete; retention is blocked.");

        var deleteIds = plan.DeleteBackupIds.ToHashSet(StringComparer.Ordinal);
        var delete = current.Where(m => deleteIds.Contains(m.BackupId)).ToArray();
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

        return new RetentionSummary(delete.Length, deletedChunks, plan.KeepBackupIds.Count);
    }

    public async Task<RetentionPlan> PlanAsync(
        string agentId,
        string sourceRoot,
        RetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        policy.Validate();
        var normalizedSource = Path.GetFullPath(sourceRoot);
        var manifests = (await repository.ListManifestsAsync(agentId, normalizedSource, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToArray();
        var keep = SelectKeepSet(manifests, policy);
        return new RetentionPlan(
            agentId,
            normalizedSource,
            DateTimeOffset.UtcNow,
            manifests.Select(m => m.BackupId).ToArray(),
            manifests.Where(m => keep.Contains(m.BackupId)).Select(m => m.BackupId).ToArray(),
            manifests.Where(m => !keep.Contains(m.BackupId)).Select(m => m.BackupId).ToArray());
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
