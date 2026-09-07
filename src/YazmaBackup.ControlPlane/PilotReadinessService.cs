using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed class PilotReadinessService
{
    private readonly IControlPlaneStore _control;
    private readonly IResilienceStore _resilience;

    public PilotReadinessService(IControlPlaneStore control, IResilienceStore resilience)
    {
        _control = control;
        _resilience = resilience;
    }

    public IReadOnlyList<PolicyTemplateDto> GetTemplates() =>
    [
        new("office-balanced", "Ofis · Dengeli", "Standart kullanıcı bilgisayarları için güvenli varsayılanlar.", "Ofis, satış, operasyon kullanıcıları",
            60, 2 * 1024 * 1024, 12 * 1024 * 1024, 7, 24,
            new RetentionPolicy(30, 14, 8, 12, 24), new ProtectionPolicy()),
        new("finance-critical", "Kritik Finans", "Daha sık yedek, daha uzun saklama ve daha sık restore doğrulaması.", "Muhasebe, finans, yönetim kritik belgeleri",
            15, 4 * 1024 * 1024, 20 * 1024 * 1024, 1, 6,
            new RetentionPolicy(96, 30, 12, 18, 72), new ProtectionPolicy(MinimumChangedFiles: 60, ChangedFileRatioThreshold: 0.25, MinimumExtensionChanges: 15, ExtensionChangeRatioThreshold: 0.12)),
        new("mobile-laptop", "Laptop · Akıllı Bant Genişliği", "Kullanıcı aktifken hafif, cihaz boşta iken daha hızlı aktarım.", "Dizüstü ve Wi-Fi kullanan saha ekipleri",
            120, 1024 * 1024, 10 * 1024 * 1024, 7, 24,
            new RetentionPolicy(24, 14, 6, 6, 24), new ProtectionPolicy()),
        new("archive-steady", "Arşiv · Ekonomik", "Daha seyrek değişen büyük klasörler için düşük gündüz trafiği.", "Arşiv, proje klasörleri, büyük veri setleri",
            360, 1024 * 1024, 16 * 1024 * 1024, 14, 24,
            new RetentionPolicy(20, 30, 12, 24, 48), new ProtectionPolicy())
    ];

    public PolicyTemplateDto? FindTemplate(string templateId) => GetTemplates().FirstOrDefault(x => string.Equals(x.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));

    public async Task<PilotReadinessSummaryDto> BuildSummaryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var agents = await _control.GetAgentsAsync(ct).ConfigureAwait(false);
        var policies = await _control.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var alarms = await _control.GetAlarmsAsync(1000, false, ct).ConfigureAwait(false);
        var repositories = await _resilience.GetRepositoryHealthAsync(1000, ct).ConfigureAwait(false);
        var recoveryRuns = await _resilience.GetRecoveryRunsAsync(200, ct).ConfigureAwait(false);

        var online = agents.Count(a => now - a.LastSeenUtc <= TimeSpan.FromMinutes(3));
        var locked = agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase));
        var critical = alarms.Count(a => a.Status != AlarmStatus.Resolved && string.Equals(a.Severity, AlarmSeverity.Critical, StringComparison.OrdinalIgnoreCase));
        var enabled = policies.Count(p => p.Enabled);
        var healthyRepos = repositories.Count(r => string.Equals(r.Status, "healthy", StringComparison.OrdinalIgnoreCase));
        var failedRuns = recoveryRuns.Count(r => string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase) && r.StartedAtUtc >= now.AddDays(-30));

        var checks = new List<PilotReadinessCheckDto>();
        Add(checks, "fleet.enrolled", "En az bir Agent kayıtlı", agents.Count > 0 ? "pass" : "fail", agents.Count > 0 ? $"{agents.Count} Agent kayıtlı." : "Pilot için en az bir Agent kaydedin.", 15);
        Add(checks, "fleet.online", "Agent bağlantı sağlığı", agents.Count > 0 && online == agents.Count ? "pass" : online > 0 ? "warn" : "fail", $"{online}/{agents.Count} Agent son 3 dakikada çevrimiçi.", 15);
        Add(checks, "policies.enabled", "Aktif yedek politikası", enabled > 0 ? "pass" : "fail", enabled > 0 ? $"{enabled} aktif politika var." : "Pilot Agent için politika oluşturun.", 15);
        Add(checks, "protection.clean", "Ransomware koruma kilitleri", locked == 0 ? "pass" : "fail", locked == 0 ? "Aktif protection lock yok." : $"{locked} Agent güvenlik kilidinde.", 15);
        Add(checks, "alarms.critical", "Kritik alarm durumu", critical == 0 ? "pass" : "warn", critical == 0 ? "Açık kritik alarm yok." : $"{critical} kritik alarm açık.", 10);
        Add(checks, "repository.health", "Repository sağlık kanıtı", healthyRepos > 0 ? "pass" : repositories.Count > 0 ? "warn" : "fail", healthyRepos > 0 ? $"{healthyRepos} sağlıklı repository ölçümü var." : "Henüz sağlıklı repository-health sonucu yok.", 15);
        Add(checks, "recovery.evidence", "Restore doğrulama kanıtı", recoveryRuns.Any(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Status, "succeeded", StringComparison.OrdinalIgnoreCase)) ? "pass" : recoveryRuns.Count > 0 ? "warn" : "fail", recoveryRuns.Count > 0 ? "Recovery run geçmişi mevcut." : "Pilot öncesi en az bir restore drill/recovery run çalıştırın.", 15);

        var score = Score(checks);
        var actions = checks.Where(x => x.Status != "pass").OrderByDescending(x => x.Weight).Select(x => x.Message).Distinct().Take(6).ToArray();
        return new PilotReadinessSummaryDto(now, score, Grade(score), agents.Count, online, enabled, critical, locked, healthyRepos, failedRuns, checks, actions);
    }

    private static void Add(List<PilotReadinessCheckDto> checks, string id, string name, string status, string message, int weight) => checks.Add(new(id, name, status, message, weight));
    private static int Score(IEnumerable<PilotReadinessCheckDto> checks)
    {
        var list = checks.ToArray();
        var total = list.Sum(x => x.Weight);
        var earned = list.Sum(x => x.Weight * (x.Status == "pass" ? 1.0 : x.Status == "warn" ? 0.5 : 0.0));
        return total == 0 ? 0 : (int)Math.Round(earned * 100.0 / total, MidpointRounding.AwayFromZero);
    }
    private static string Grade(int score) => score >= 90 ? "Pilot hazır" : score >= 75 ? "Pilot adayı" : score >= 60 ? "Hazırlık gerekiyor" : "Hazır değil";
}
