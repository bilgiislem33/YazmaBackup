using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using YazmaBackup.Application;
using YazmaBackup.Contracts;
using YazmaBackup.Infrastructure;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class DeepValidationProbe
{
    private readonly RepositoryKeyStore _keys;
    private readonly RepositoryCircuitBreaker _circuits;

    public DeepValidationProbe(RepositoryKeyStore keys, RepositoryCircuitBreaker circuits)
    {
        _keys = keys;
        _circuits = circuits;
    }

    public async Task<DeepValidationResultDto> RunAsync(DeepValidationProbePayload payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryId);
        var started = DateTimeOffset.UtcNow;
        var checks = new List<ValidationCheckDto>();
        var source = Path.GetFullPath(payload.SourcePath);
        var repositoryRoot = Path.GetFullPath(payload.RepositoryRoot);

        await MeasureAsync(checks, "windows.source", "Windows", "Kaynak klasör erişimi", async () =>
        {
            await Task.Yield();
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            return ("pass", "Kaynak klasör erişilebilir.", source, (string?)null);
        });

        await MeasureAsync(checks, "windows.vss", "Windows", "Gerçek VSS snapshot", async () =>
        {
            if (!payload.RequireSnapshot) return ("pass", "Politika VSS zorunlu değil.", (string?)null, (string?)null);
            await using var snapshot = await new WindowsVssSnapshotProvider().CreateAsync(source, ct).ConfigureAwait(false);
            if (!snapshot.SnapshotBacked || !Directory.Exists(snapshot.ReadablePath)) throw new InvalidDataException("VSS snapshot okunabilir değil.");
            return ("pass", "VSS snapshot oluşturuldu, okunabildi ve cleanup aşamasına ulaştı.", snapshot.ReadablePath, (string?)null);
        });

        await MeasureAsync(checks, "windows.usn", "Windows", "USN Journal capture", async () =>
        {
            var tracker = new WindowsUsnJournalChangeTracker(AgentPaths.ChangeTrackingDirectory);
            var set = await tracker.CaptureAsync(source, ct).ConfigureAwait(false);
            return (set.CanReuseUnchangedFiles ? "pass" : "warn",
                set.CanReuseUnchangedFiles ? "USN artımlı değişiklik takibi kullanılabilir." : "USN güvenli reuse sağlayamadı; tam tarama fallback kullanılacak.",
                set.Mode,
                set.CanReuseUnchangedFiles ? null : "NTFS/USN journal durumunu kontrol edin; fallback veri bütünlüğünü korur.");
        });

        await MeasureAsync(checks, "repository.write", "NAS", "Write-through yazma/silme", async () =>
        {
            Directory.CreateDirectory(repositoryRoot);
            var testPath = Path.Combine(repositoryRoot, $".yazmabackup-validation-{Guid.NewGuid():N}.tmp");
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024);
            try
            {
                await using (var fs = new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Delete(testPath);
                return ("pass", "NAS write-through ve silme testi başarılı.", "65536 byte", (string?)null);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                try { if (File.Exists(testPath)) File.Delete(testPath); } catch { }
            }
        });

        await MeasureAsync(checks, "repository.capacity", "NAS", "Boş alan", async () =>
        {
            await Task.Yield();
            if (!TryGetFreeBytes(repositoryRoot, out var freeBytes))
                return ("warn", "Boş alan Windows SMB/volume API üzerinden ölçülemedi.", (string?)null, "NAS bağlantısını ve servis hesabı erişimini doğrulayın.");
            var status = freeBytes >= (ulong)Math.Max(1, payload.MinimumFreeBytes) ? "pass" : "warn";
            return (status, status == "pass" ? "Asgari boş alan mevcut." : "Boş alan pilot eşiğinin altında.", $"{freeBytes} byte", status == "warn" ? "Repository kapasitesini artırın veya retention politikasını gözden geçirin." : null);
        });

        await MeasureAsync(checks, "repository.key", "Güvenlik", "AES repository anahtarı", async () =>
        {
            await Task.Yield();
            var summary = _keys.GetSummary(payload.RepositoryId);
            return ("pass", "Repository şifreleme anahtarı hazır.", $"ActiveKey={summary.ActiveKeyId}; Count={summary.KeyIds.Count}", (string?)null);
        });

        await MeasureAsync(checks, "repository.circuit", "Dayanıklılık", "Repository circuit", async () =>
        {
            await Task.Yield();
            var row = _circuits.Snapshot().FirstOrDefault(x => string.Equals(x.RepositoryId, payload.RepositoryId, StringComparison.Ordinal));
            if (row is null || row.ConsecutiveFailures == 0) return ("pass", "Repository circuit sağlıklı.", (string?)null, (string?)null);
            return (row.IsOpen(DateTimeOffset.UtcNow) ? "fail" : "warn", $"Repository için {row.ConsecutiveFailures} ardışık hata kaydı var.", row.LastError, "Önce NAS erişimini doğrulayın; sorun giderildiyse güvenli circuit reset uygulanabilir.");
        });

        var score = Score(checks);
        return new DeepValidationResultDto(started, DateTimeOffset.UtcNow, score, Grade(score), Environment.MachineName, checks);
    }

    private static async Task MeasureAsync(List<ValidationCheckDto> checks, string id, string category, string name,
        Func<Task<(string Status, string Message, string? Detail, string? Action)>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            checks.Add(new ValidationCheckDto(id, category, name, result.Status, result.Message, sw.ElapsedMilliseconds, result.Detail, result.Action));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            checks.Add(new ValidationCheckDto(id, category, name, "fail", "Kontrol başarısız.", sw.ElapsedMilliseconds, Sanitize(ex.Message), "Detayı inceleyin ve kontrolü yeniden çalıştırın."));
        }
    }

    private static bool TryGetFreeBytes(string path, out ulong freeBytes)
    {
        freeBytes = 0;
        var normalized = Path.GetFullPath(path);
        if (!GetDiskFreeSpaceEx(normalized, out var available, out _, out _)) return false;
        freeBytes = available;
        return true;
    }

    #pragma warning disable SYSLIB1054 // Narrow Win32 capacity probe; avoids generated unsafe interop in the Agent project.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
#pragma warning restore SYSLIB1054

    private static int Score(List<ValidationCheckDto> checks)
    {
        if (checks.Count == 0) return 0;
        var points = checks.Sum(x => x.Status == "pass" ? 100 : x.Status == "warn" ? 50 : 0);
        return (int)Math.Round(points / (double)checks.Count, MidpointRounding.AwayFromZero);
    }

    private static string Grade(int score) => score >= 90 ? "Hazır" : score >= 75 ? "Pilot için uygun" : score >= 60 ? "Düzeltme gerekli" : "Hazır değil";
    private static string Sanitize(string value)
    {
        var s = string.Join(" ", (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length > 500 ? s[..500] : s;
    }
}
