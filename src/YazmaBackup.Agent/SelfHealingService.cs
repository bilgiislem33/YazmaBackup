using System.Runtime.Versioning;
using YazmaBackup.Contracts;
using YazmaBackup.Infrastructure;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class SelfHealingService
{
    private readonly RepositoryCircuitBreaker _circuits;
    public SelfHealingService(RepositoryCircuitBreaker circuits) => _circuits = circuits;

    public SelfHealingDiagnosisDto Diagnose(SelfHealingDiagnosePayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.RepositoryId);
        var findings = new List<ValidationCheckDto>();
        var actions = new List<SelfHealingActionDto>();
        var circuit = _circuits.Snapshot().FirstOrDefault(x => string.Equals(x.RepositoryId, payload.RepositoryId, StringComparison.Ordinal));
        if (circuit is { ConsecutiveFailures: > 0 })
        {
            findings.Add(new ValidationCheckDto("heal.circuit", "Dayanıklılık", "Repository circuit", circuit.IsOpen(DateTimeOffset.UtcNow) ? "fail" : "warn", $"{circuit.ConsecutiveFailures} ardışık repository hatası kayıtlı.", 0, circuit.LastError, "NAS erişimi düzeldiyse circuit reset uygulanabilir."));
            actions.Add(new SelfHealingActionDto("reset-repository-circuit", "Repository circuit'i sıfırla", "Yalnız hata sayacı/backoff state'ini temizler; yedek verisine dokunmaz.", true, "low"));
        }
        else
        {
            findings.Add(new ValidationCheckDto("heal.circuit", "Dayanıklılık", "Repository circuit", "pass", "Repository circuit sağlıklı.", 0));
        }

        if (!string.IsNullOrWhiteSpace(payload.SourcePath) && Directory.Exists(payload.SourcePath))
            findings.Add(new ValidationCheckDto("heal.source", "Kaynak", "Kaynak erişimi", "pass", "Kaynak klasör erişilebilir.", 0, Path.GetFullPath(payload.SourcePath)));
        else
            findings.Add(new ValidationCheckDto("heal.source", "Kaynak", "Kaynak erişimi", "fail", "Kaynak klasör erişilemiyor.", 0, payload.SourcePath, "Klasör yolunu ve servis hesabı erişimini doğrulayın."));

        if (!string.IsNullOrWhiteSpace(payload.SourcePath) && Directory.Exists(payload.SourcePath))
        {
            var stale = CountStaleTempFiles(payload.SourcePath);
            findings.Add(new ValidationCheckDto("heal.temp", "Temizlik", "Eski geçici restore dosyaları", stale == 0 ? "pass" : "warn", stale == 0 ? "Eski geçici dosya bulunmadı." : $"{stale} adet 24 saatten eski YazmaBackup restore temp dosyası bulundu.", 0));
            if (stale > 0) actions.Add(new SelfHealingActionDto("cleanup-stale-restore-temp", "Eski restore temp dosyalarını temizle", "Yalnız .yazmabackup-restore-*.tmp desenindeki 24 saatten eski dosyaları siler.", true, "low"));
        }

        var overall = findings.Any(x => x.Status == "fail") ? "needs-attention" : findings.Any(x => x.Status == "warn") ? "warning" : "healthy";
        return new SelfHealingDiagnosisDto(DateTimeOffset.UtcNow, overall, findings, actions);
    }

    public SelfHealingApplyResultDto Apply(SelfHealingApplyPayload payload)
    {
        if (payload.ActionId == "reset-repository-circuit")
        {
            _circuits.ReportSuccess(payload.RepositoryId);
            return new SelfHealingApplyResultDto(payload.ActionId, true, "Repository circuit state güvenli biçimde sıfırlandı.", DateTimeOffset.UtcNow);
        }
        if (payload.ActionId == "cleanup-stale-restore-temp")
        {
            if (string.IsNullOrWhiteSpace(payload.WorkingRoot)) throw new ArgumentException("WorkingRoot is required for temp cleanup.");
            var deleted = CleanupStaleTempFiles(payload.WorkingRoot);
            return new SelfHealingApplyResultDto(payload.ActionId, true, $"{deleted} eski restore temp dosyası temizlendi.", DateTimeOffset.UtcNow);
        }
        throw new InvalidOperationException("Action is not allowlisted for automatic self-healing.");
    }

    private static int CountStaleTempFiles(string root) => EnumerateSafeTempFiles(root).Count();
    private static int CleanupStaleTempFiles(string root)
    {
        var deleted = 0;
        foreach (var file in EnumerateSafeTempFiles(root)) { File.Delete(file); deleted++; }
        return deleted;
    }
    private static IEnumerable<string> EnumerateSafeTempFiles(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) yield break;
        foreach (var file in Directory.EnumerateFiles(full, "*.yazmabackup-restore-*.tmp", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24)) yield return file;
        }
    }
}
