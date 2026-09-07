using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record PredictiveRiskRecord(
    string Id, string Severity, string RiskType, Guid? AgentId, string? MachineName,
    string Title, string Prediction, int ConfidencePct, DateTimeOffset? ExpectedAtUtc,
    string RecommendedAction, bool RequiresApproval);

public sealed record PredictiveProtectionSummary(
    DateTimeOffset GeneratedAtUtc, int PredictiveHealthScore, string Health,
    int SlaBreachPredictions, int CapacityPredictions, int RestoreEvidenceWarnings,
    int AgentStabilityWarnings, IReadOnlyList<PredictiveRiskRecord> Predictions);

public sealed class PredictiveProtectionService
{
    private readonly IControlPlaneStore _store;
    private readonly IResilienceStore _resilience;

    public PredictiveProtectionService(IControlPlaneStore store, IResilienceStore resilience)
    {
        _store=store;
        _resilience=resilience;
    }

    public async Task<PredictiveProtectionSummary> BuildAsync(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var agents=await _store.GetAgentsAsync(ct).ConfigureAwait(false);
        var policies=await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var commands=await _store.GetRecentCommandsAsync(1000,ct).ConfigureAwait(false);
        var health=await _resilience.GetRepositoryHealthAsync(1000,ct).ConfigureAwait(false);
        var recovery=await _resilience.GetRecoveryRunsAsync(1000,ct).ConfigureAwait(false);
        var risks=new List<PredictiveRiskRecord>();

        // Predict likely SLA miss before the policy is actually overdue.
        foreach(var policy in policies.Where(p=>p.Enabled))
        {
            var agent=agents.FirstOrDefault(a=>a.AgentId==policy.AgentId);
            var recent=commands.Where(c=>c.AgentId==policy.AgentId && c.Type==AgentCommandType.BackupPath &&
                c.CompletedAtUtc>=now.AddDays(-7)).OrderByDescending(c=>c.CompletedAtUtc).Take(10).ToArray();
            var failures=recent.Count(c=>!c.Succeeded);
            var offline=agent is null || agent.LastSeenUtc<now.AddMinutes(-3);
            var minutesToRun=(policy.NextRunAtUtc-now).TotalMinutes;
            var failureRatio=recent.Length==0?0d:(double)failures/recent.Length;
            var risk=(offline?55:0)+(failureRatio*35)+(minutesToRun<=Math.Max(10,policy.IntervalMinutes*.20)?10:0);
            if(risk>=45 && policy.NextRunAtUtc>=now)
            {
                var confidence=(int)Math.Round(Math.Clamp(45+recent.Length*4+failureRatio*25+(offline?20:0),45,95));
                risks.Add(new($"sla:{policy.PolicyId:N}","warning","sla-breach",policy.AgentId,agent?.MachineName,
                    "Yedekleme SLA riski yükseliyor",
                    $"{policy.Name} politikasının sıradaki çalışmasının sorun yaşama ihtimali yükseldi. Agent bağlantısı ve son yedekleme geçmişi risk gösteriyor.",
                    confidence,policy.NextRunAtUtc,"Plan zamanı gelmeden Agent bağlantısını ve repository erişimini teşhis et.",false));
            }
        }

        // Capacity prediction uses measured growth, not a fabricated trend.
        foreach(var h in health.GroupBy(x=>x.RepositoryId,StringComparer.OrdinalIgnoreCase).Select(g=>g.OrderByDescending(x=>x.MeasuredAtUtc).First()))
        {
            if(h.EstimatedDaysToFull is >0 and <=45)
            {
                var expected=now.AddDays(h.EstimatedDaysToFull.Value);
                var sev=h.EstimatedDaysToFull<=14?"critical":"warning";
                risks.Add(new($"capacity-predict:{h.RepositoryId}",sev,"capacity",h.AgentId,
                    agents.FirstOrDefault(a=>a.AgentId==h.AgentId)?.MachineName,
                    "NAS kapasite eşiğine yaklaşıyor",
                    $"{h.RepositoryId} mevcut ölçülen büyüme hızı sürerse yaklaşık {expected:yyyy-MM-dd} tarihinde dolabilir.",
                    h.DailyGrowthBytes>0?90:60,expected,"Kapasite artışı veya retention planını doluluk oluşmadan değerlendir.",true));
            }
        }

        // Restore evidence becomes a risk before evidence is too old.
        foreach(var policy in policies.Where(p=>p.Enabled))
        {
            var last=recovery.Where(r=>r.Targets.Any(t=>t.PolicyId==policy.PolicyId) && r.CompletedAtUtc is not null)
                .OrderByDescending(r=>r.CompletedAtUtc).FirstOrDefault();
            var ageDays=last?.CompletedAtUtc is null?double.PositiveInfinity:(now-last.CompletedAtUtc.Value).TotalDays;
            var warningAge=Math.Max(1,policy.RestoreDrillIntervalDays*.80);
            if(ageDays>=warningAge)
            {
                var agent=agents.FirstOrDefault(a=>a.AgentId==policy.AgentId);
                risks.Add(new($"restore-evidence:{policy.PolicyId:N}",ageDays>=policy.RestoreDrillIntervalDays?"critical":"warning",
                    "restore-evidence",policy.AgentId,agent?.MachineName,
                    "Restore kanıtı eskimek üzere",
                    last is null?"Bu politika için tamamlanmış restore kanıtı bulunamadı.":$"Son restore kanıtı {ageDays:0.#} günlük; hedef aralık {policy.RestoreDrillIntervalDays} gün.",
                    last is null?95:85,last?.CompletedAtUtc?.AddDays(policy.RestoreDrillIntervalDays),
                    "Planlı restore drill çalıştır ve doğrulama kanıtını yenile.",false));
            }
        }

        // Agent stability: repeated recent disconnect state + backup failures is an early warning.
        foreach(var agent in agents)
        {
            var recent=commands.Where(c=>c.AgentId==agent.AgentId && c.CreatedAtUtc>=now.AddHours(-24)).ToArray();
            var failed=recent.Count(c=>c.CompletedAtUtc is not null && !c.Succeeded);
            if(agent.LastSeenUtc<now.AddMinutes(-3) && failed>=2)
                risks.Add(new($"agent-stability:{agent.AgentId:N}","warning","agent-stability",agent.AgentId,agent.MachineName,
                    "Agent kararlılık riski",
                    $"Agent şu anda çevrimdışı ve son 24 saatte {failed} tamamlanmış komut başarısızlığı var.",
                    Math.Min(95,65+failed*5),null,"Agent servis sağlığı, ağ ve Control Plane erişimini planlı olarak teşhis et.",false));
        }

        var sla=risks.Count(r=>r.RiskType=="sla-breach");
        var capacity=risks.Count(r=>r.RiskType=="capacity");
        var restore=risks.Count(r=>r.RiskType=="restore-evidence");
        var stability=risks.Count(r=>r.RiskType=="agent-stability");
        var critical=risks.Count(r=>r.Severity=="critical");
        var score=Math.Clamp(100-critical*10-(risks.Count-critical)*3,0,100);
        return new(now,score,score>=90?"healthy":score>=75?"attention":"critical",sla,capacity,restore,stability,
            risks.OrderBy(r=>r.Severity=="critical"?0:1).ThenByDescending(r=>r.ConfidencePct).Take(300).ToArray());
    }
}
