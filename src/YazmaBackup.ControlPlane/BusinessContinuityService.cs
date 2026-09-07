using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record BusinessServiceContinuity(
    string ServiceId, string Name, string Criticality, int Priority,
    int PolicyCount, int RecoveryPlanCount, int RtoTargetMinutes,
    int ReadinessScore, string Readiness, int MissingRecoveryCoverage,
    IReadOnlyList<string> Dependencies, string RecoveryOrderReason);

public sealed record DisasterSimulationStep(
    int Order, string ServiceId, string ServiceName, string Criticality,
    int RtoTargetMinutes, string Action, string Gate);

public sealed record BusinessContinuitySummary(
    DateTimeOffset GeneratedAtUtc, int ContinuityScore, string ContinuityReadiness,
    int CriticalServices, int NotReadyCriticalServices, int UncoveredPolicies,
    IReadOnlyList<BusinessServiceContinuity> Services,
    IReadOnlyList<DisasterSimulationStep> DisasterSimulation);

public sealed class BusinessContinuityService
{
    private readonly IControlPlaneStore _store;
    private readonly IResilienceStore _resilience;
    private readonly RecoveryFabricService _fabric;

    public BusinessContinuityService(IControlPlaneStore store, IResilienceStore resilience, RecoveryFabricService fabric)
    {
        _store=store; _resilience=resilience; _fabric=fabric;
    }

    public async Task<BusinessContinuitySummary> BuildAsync(CancellationToken ct)
    {
        var policies=await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var plans=await _resilience.GetRecoveryPlansAsync(ct).ConfigureAwait(false);
        var fabric=await _fabric.BuildAsync(ct).ConfigureAwait(false);

        // R23 intentionally derives business-service grouping from explicit policy naming.
        // No department/business ownership is invented when metadata does not exist.
        var groups=policies.GroupBy(p=>BusinessKey(p.Name),StringComparer.OrdinalIgnoreCase).ToArray();
        var services=new List<BusinessServiceContinuity>();
        foreach(var group in groups)
        {
            var ids=group.Select(p=>p.PolicyId).ToHashSet();
            var related=plans.Where(pl=>pl.PolicyIds.Any(ids.Contains)).ToArray();
            var fabricPlans=fabric.Plans.Where(fp=>related.Any(r=>r.PlanId==fp.PlanId)).ToArray();
            var covered=ids.Count(id=>related.Any(r=>r.PolicyIds.Contains(id)));
            var missing=Math.Max(0,ids.Count-covered);
            var score=fabricPlans.Length==0?0:(int)Math.Round(fabricPlans.Average(x=>x.ReadinessScore));
            if(missing>0) score=Math.Max(0,score-Math.Min(40,missing*10));
            var criticality=InferCriticality(group.Key,group.Count(),related);
            var priority=criticality=="critical"?1:criticality=="high"?2:3;
            var rto=related.Length==0?0:related.Min(x=>x.RtoTargetMinutes);
            services.Add(new(
                Slug(group.Key),group.Key,criticality,priority,ids.Count,related.Length,rto,
                score,score>=90?"ready":score>=70?"attention":"not-ready",missing,
                Array.Empty<string>(),
                related.Length==0?"Recovery Plan kapsamı yok; önce DR planı tanımlanmalı.":
                missing>0?"Bazı backup politikaları Recovery Plan dışında; kapsam tamamlanmalı.":
                "Öncelik kritik seviye ve tanımlı RTO hedefinden türetilir."));
        }

        var ordered=services.OrderBy(x=>x.Priority).ThenBy(x=>x.RtoTargetMinutes==0?int.MaxValue:x.RtoTargetMinutes)
            .ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        var simulation=ordered.Select((s,i)=>new DisasterSimulationStep(
            i+1,s.ServiceId,s.Name,s.Criticality,s.RtoTargetMinutes,
            s.RecoveryPlanCount==0?"Önce Recovery Plan oluştur; otomatik recovery başlatılmaz.":
            "Recovery Plan hedeflerini sırayla doğrula ve restore kanıtına göre ilerle.",
            s.Readiness=="ready"?"RTO + restore kanıtı uygun":"İnsan onayı / düzeltme gerekli")).ToArray();

        var critical=services.Count(x=>x.Criticality=="critical");
        var notReady=services.Count(x=>x.Criticality=="critical" && x.Readiness!="ready");
        var uncovered=services.Sum(x=>x.MissingRecoveryCoverage);
        var score=services.Count==0?0:(int)Math.Round(services.Average(x=>x.ReadinessScore));
        return new(DateTimeOffset.UtcNow,score,score>=90?"ready":score>=70?"attention":"not-ready",
            critical,notReady,uncovered,ordered,simulation);
    }

    private static string BusinessKey(string policyName)
    {
        var value=(policyName??string.Empty).Trim();
        if(value.Length==0) return "Tanımsız İş Servisi";
        foreach(var separator in new[]{" - "," / "," | ",":","_"})
        {
            var i=value.IndexOf(separator,StringComparison.Ordinal);
            if(i>1) return value[..i].Trim();
        }
        return value;
    }

    private static string InferCriticality(string name,int policyCount,IReadOnlyList<RecoveryPlanRecord> plans)
    {
        // Criticality is only elevated from concrete operational signals: aggressive RTO or multi-policy recovery scope.
        if(plans.Any(x=>x.RtoTargetMinutes>0 && x.RtoTargetMinutes<=30)) return "critical";
        if(plans.Any(x=>x.RtoTargetMinutes>0 && x.RtoTargetMinutes<=120) || policyCount>=3) return "high";
        return "standard";
    }

    private static string Slug(string value)
    {
        var chars=value.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)?c:'-').ToArray();
        return string.Join("-",new string(chars).Split('-',StringSplitOptions.RemoveEmptyEntries)).Trim('-');
    }
}
