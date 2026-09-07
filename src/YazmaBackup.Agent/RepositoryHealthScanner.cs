using System.Runtime.InteropServices;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.Agent;

internal static partial class RepositoryHealthScanner
{
    public static RepositoryHealthScanResultDto Scan(RepositoryHealthScanPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryRoot);
        var root = Path.GetFullPath(payload.RepositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository root is not accessible: {root}");

        var measuredAt = DateTimeOffset.UtcNow;
        var (total, free) = GetCapacity(root);
        long physicalBytes = 0;
        long recentBytes = 0;
        var cutoff = measuredAt.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            physicalBytes = checked(physicalBytes + info.Length);
            if (info.LastWriteTimeUtc >= cutoff.UtcDateTime) recentBytes = checked(recentBytes + info.Length);
        }

        var manifestRoot = Path.Combine(root, "manifests");
        var manifests = Directory.Exists(manifestRoot)
            ? Directory.EnumerateFiles(manifestRoot, "*", SearchOption.AllDirectories)
                .Where(x => x.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(x => new FileInfo(x)).ToArray()
            : [];
        var restorePointCount = manifests.Length;
        var latest = manifests.Length == 0 ? (DateTimeOffset?)null : new DateTimeOffset(manifests.Max(x => x.LastWriteTimeUtc), TimeSpan.Zero);
        var dailyGrowth = recentBytes / 7d;
        double? daysToFull = dailyGrowth > 0 ? free / dailyGrowth : null;
        var freeRatio = total > 0 ? free / (double)total : 0;

        var status = RepositoryHealthStatus.Healthy;
        string? reason = null;
        if (restorePointCount == 0)
        {
            status = RepositoryHealthStatus.Critical;
            reason = "Repository içinde restore point bulunamadı.";
        }
        else if (freeRatio < 0.05 || daysToFull is < 3)
        {
            status = RepositoryHealthStatus.Critical;
            reason = "Repository kapasitesi kritik seviyede.";
        }
        else if (freeRatio < 0.15 || daysToFull is < 14)
        {
            status = RepositoryHealthStatus.Warning;
            reason = "Repository kapasitesi uyarı seviyesinde.";
        }

        return new RepositoryHealthScanResultDto(payload.PolicyId, root, payload.RepositoryId, measuredAt, total, free, physicalBytes, restorePointCount, latest, recentBytes, dailyGrowth, daysToFull, status, reason);
    }

    private static (long TotalBytes, long FreeBytes) GetCapacity(string root)
    {
        if (OperatingSystem.IsWindows() && GetDiskFreeSpaceEx(root, out var available, out var total, out _))
            return (checked((long)total), checked((long)available));
        var drive = new DriveInfo(Path.GetPathRoot(root) ?? root);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
