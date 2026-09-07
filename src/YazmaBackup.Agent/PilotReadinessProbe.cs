using System.Runtime.Versioning;
using YazmaBackup.Contracts;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class PilotReadinessProbe
{
    private readonly RepositoryKeyStore _repositoryKeys;

    public PilotReadinessProbe(RepositoryKeyStore repositoryKeys)
    {
        _repositoryKeys = repositoryKeys;
    }

    public PilotReadinessProbeResultDto Run(PilotReadinessProbePayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryId);

        var checks = new List<PilotReadinessCheckDto>();
        var source = Path.GetFullPath(payload.SourcePath);
        var repositoryRoot = Path.GetFullPath(payload.RepositoryRoot);

        Add(checks, "os.windows", "Windows Agent", OperatingSystem.IsWindows() ? "pass" : "fail",
            OperatingSystem.IsWindows() ? "Windows çalışma ortamı doğrulandı." : "Bu pilot profili Windows Agent gerektiriyor.", 10);

        var sourceExists = Directory.Exists(source);
        Add(checks, "source.exists", "Kaynak klasör", sourceExists ? "pass" : "fail",
            sourceExists ? "Yedeklenecek klasör erişilebilir." : "Yedeklenecek klasör bulunamadı veya erişilemiyor.", 15, source);

        if (sourceExists)
        {
            var local = TryGetDrive(source);
            var format = TryDriveFormat(local);
            var ntfs = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
            Add(checks, "source.ntfs", "NTFS / USN uygunluğu", ntfs ? "pass" : "warn",
                ntfs ? "Kaynak NTFS; USN Journal artımlı takip için uygun." : "Kaynak NTFS olarak doğrulanamadı; tam tarama fallback kullanılabilir.", 10, format);

            var vssEligible = OperatingSystem.IsWindows() && local is { DriveType: DriveType.Fixed };
            var vssStatus = !payload.RequireSnapshot ? "pass" : vssEligible ? "pass" : "fail";
            Add(checks, "source.vss", "VSS uygunluğu", vssStatus,
                !payload.RequireSnapshot ? "Bu politika VSS zorunlu değil." : vssEligible ? "Yerel sabit disk VSS snapshot için uygun görünüyor." : "VSS için yerel sabit disk doğrulanamadı.", 15);
        }

        var repositoryWrite = ProbeRepositoryWrite(repositoryRoot, out var repositoryError);
        Add(checks, "repository.write", "NAS yazma testi", repositoryWrite ? "pass" : "fail",
            repositoryWrite ? "Repository üzerinde küçük yazma/silme testi başarılı." : "Repository yazma testi başarısız.", 20, repositoryError);

        var freeBytes = TryGetFreeBytes(repositoryRoot);
        if (freeBytes is null)
        {
            Add(checks, "repository.capacity", "NAS boş alan", "warn", "Boş alan değeri okunamadı.", 10);
        }
        else
        {
            var enough = freeBytes.Value >= Math.Max(1, payload.MinimumFreeBytes);
            Add(checks, "repository.capacity", "NAS boş alan", enough ? "pass" : "warn",
                enough ? "Pilot için asgari boş alan mevcut." : "Boş alan pilot eşiğinin altında.", 10, $"{freeBytes.Value} byte boş");
        }

        try
        {
            var summary = _repositoryKeys.GetSummary(payload.RepositoryId);
            Add(checks, "repository.key", "Repository şifreleme anahtarı", "pass",
                "Aktif AES repository anahtarı hazır.", 15, $"ActiveKey={summary.ActiveKeyId}; KeyCount={summary.KeyIds.Count}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Add(checks, "repository.key", "Repository şifreleme anahtarı", "fail",
                "Bu Agent için repository anahtarı henüz provision edilmemiş.", 15, ex.Message);
        }

        var score = Score(checks);
        return new PilotReadinessProbeResultDto(DateTimeOffset.UtcNow, score, Grade(score), Environment.MachineName,
            source, repositoryRoot, payload.RepositoryId, checks);
    }

    private static bool ProbeRepositoryWrite(string root, out string? error)
    {
        error = null;
        string? path = null;
        try
        {
            Directory.CreateDirectory(root);
            path = Path.Combine(root, $".yazmabackup-pilot-probe-{Guid.NewGuid():N}.tmp");
            var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
            return false;
        }
    }

    private static DriveInfo? TryGetDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
        }
        catch { return null; }
    }

    private static string? TryDriveFormat(DriveInfo? drive)
    {
        try { return drive is { IsReady: true } ? drive.DriveFormat : null; }
        catch { return null; }
    }

    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            var drive = TryGetDrive(path);
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch { return null; }
    }

    private static void Add(List<PilotReadinessCheckDto> checks, string id, string name, string status, string message, int weight, string? detail = null) =>
        checks.Add(new PilotReadinessCheckDto(id, name, status, message, weight, detail));

    private static int Score(IEnumerable<PilotReadinessCheckDto> checks)
    {
        var list = checks.ToArray();
        var total = list.Sum(x => x.Weight);
        if (total <= 0) return 0;
        var earned = list.Sum(x => x.Weight * (x.Status == "pass" ? 1.0 : x.Status == "warn" ? 0.5 : 0.0));
        return (int)Math.Round(earned * 100.0 / total, MidpointRounding.AwayFromZero);
    }

    private static string Grade(int score) => score >= 90 ? "Hazır" : score >= 75 ? "Pilot için uygun" : score >= 60 ? "Düzeltme gerekli" : "Hazır değil";
}
