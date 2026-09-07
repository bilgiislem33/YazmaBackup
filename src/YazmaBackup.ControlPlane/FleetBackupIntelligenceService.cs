using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record FleetRiskItem(
    string Id, string Severity, string Category, Guid? AgentId, string? MachineName,
    string Title, string Detail, string RecommendedAction, bool CanAutoDiagnose, bool RequiresApproval);

public sealed record FleetBackupIntelligenceSummary(
    DateTimeOffset GeneratedAtUtc, int FleetScore, string Health,
    int TotalAgents, int OnlineAgents, int ProtectedAgents, int EnabledPolicies,
    int BackupAttempts24h, int BackupFailures24h, double BackupSuccessPct,
    int CriticalRisks, int WarningRisks, IReadOnlyList<FleetRiskItem> Risks,
    IReadOnlyList<string> Priorities);

public sealed class FleetBackupIntelligenceService
{
    private readonly IControlPlaneStore _store;
    private readonly IResilienceStore _resilience;

    public FleetBackupIntelligenceService(IControlPlaneStore store, IResilienceStore resilience)
    {
        _store = store;
        _resilience = resilience;
    }

    public async Task<FleetBackupIntelligenceSummary> BuildAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var agents = await _store.GetAgentsAsync(ct).ConfigureAwait(false);
        var policies = await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var commands = await _store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
        var health = await _resilience.GetRepositoryHealthAsync(1000, ct).ConfigureAwait(false);
        var onlineCutoff = now.AddMinutes(-3);
        var risks = new List<FleetRiskItem>();

        foreach (var a in agents.Where(x => x.LastSeenUtc < onlineCutoff))
            risks.Add(new($"offline:{a.AgentId:N}","warning","agent-offline",a.AgentId,a.MachineName,
                "Bilgisayar yedekleme merkezine ulaşmıyor",
                $"Son bağlantı {a.LastSeenUtc:O}. Bu bilgisayar yeni yedekleme görevlerini alamaz.",
                "Agent bağlantısını ve MeshCentral erişimini kontrol et.",true,false));

        foreach (var a in agents.Where(x => string.Equals(x.ProtectionStatus,"locked",StringComparison.OrdinalIgnoreCase)))
            risks.Add(new($"locked:{a.AgentId:N}","critical","protection-locked",a.AgentId,a.MachineName,
                "Koruma kilidi aktif",
                "Bu bilgisayarda koruma kilidi aktif. Yedekleme işlemleri etkilenebilir.",
                "Kilidin nedenini incele; otomatik kaldırma yapma.",true,true));

        foreach (var p in policies.Where(x => x.Enabled && x.NextRunAtUtc < now.AddMinutes(-Math.Max(10,x.IntervalMinutes))))
        {
            var machine=agents.FirstOrDefault(a=>a.AgentId==p.AgentId)?.MachineName;
            risks.Add(new($"overdue:{p.PolicyId:N}","warning","policy-overdue",p.AgentId,machine,
                "Yedekleme zamanı geçmiş",
                $"{p.Name} politikası beklenen zamanda çalışmamış görünüyor.",
                "Önce Agent ve repository erişimini teşhis et.",true,false));
        }

        var failures=commands.Where(c=>c.Type==AgentCommandType.BackupPath && c.CompletedAtUtc>=now.AddHours(-24) && !c.Succeeded).ToArray();
        foreach(var c in failures.Take(100))
        {
            var machine=agents.FirstOrDefault(a=>a.AgentId==c.AgentId)?.MachineName;
            risks.Add(new($"backup-failed:{c.CommandId:N}","critical","backup-failed",c.AgentId,machine,
                "Son yedekleme başarısız",
                string.IsNullOrWhiteSpace(c.Error) ? "Yedekleme başarısız oldu; ayrıntılı teşhis gerekli." : c.Error!,
                "Hata ayrıntısını incele ve güvenli teşhis çalıştır.",true,true));
        }

        foreach(var h in health.GroupBy(x=>x.RepositoryId,StringComparer.OrdinalIgnoreCase).Select(g=>g.OrderByDescending(x=>x.MeasuredAtUtc).First()))
        {
            if(h.EstimatedDaysToFull is <= 14)
                risks.Add(new($"capacity:{h.RepositoryId}","critical","repository-capacity",h.AgentId,
                    agents.FirstOrDefault(a=>a.AgentId==h.AgentId)?.MachineName,
                    "NAS alanı kritik seviyeye yaklaşıyor",
                    $"{h.RepositoryId}: tahmini {h.EstimatedDaysToFull:0.#} gün içinde dolabilir.",
                    "Kapasite artırımı veya retention düzenlemesi planla.",false,true));
            else if(h.EstimatedDaysToFull is <= 30)
                risks.Add(new($"capacity:{h.RepositoryId}","warning","repository-capacity",h.AgentId,
                    agents.FirstOrDefault(a=>a.AgentId==h.AgentId)?.MachineName,
                    "NAS kapasitesi azalıyor",
                    $"{h.RepositoryId}: tahmini {h.EstimatedDaysToFull:0.#} günlük kapasite kaldı.",
                    "Kapasite planlamasını başlat.",false,false));
        }

        var attempts=commands.Count(c=>c.Type==AgentCommandType.BackupPath && c.CompletedAtUtc>=now.AddHours(-24));
        var failed=failures.Length;
        var successPct=attempts==0?100d:Math.Round(100d*(attempts-failed)/attempts,1);
        var online=agents.Count(a=>a.LastSeenUtc>=onlineCutoff);
        var onlinePct=agents.Count==0?100d:100d*online/agents.Count;
        var critical=risks.Count(r=>r.Severity=="critical");
        var warning=risks.Count(r=>r.Severity=="warning");
        var score=(int)Math.Round(Math.Clamp(successPct*.50+onlinePct*.25+(critical==0?15:Math.Max(0,15-critical*3))+(warning==0?10:Math.Max(0,10-warning)),0,100));
        var priorities=risks.OrderBy(r=>r.Severity=="critical"?0:1).Take(5).Select(r=>$"{r.Title} · {r.MachineName ?? r.Category}").ToArray();

        return new(now,score,score>=95?"healthy":score>=80?"attention":"critical",
            agents.Count,online,agents.Count(a=>!string.Equals(a.ProtectionStatus,"locked",StringComparison.OrdinalIgnoreCase)),
            policies.Count(p=>p.Enabled),attempts,failed,successPct,critical,warning,
            risks.OrderBy(r=>r.Severity=="critical"?0:1).ThenBy(r=>r.MachineName).Take(300).ToArray(),priorities);
    }
}
