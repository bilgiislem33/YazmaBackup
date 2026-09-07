using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record RecoveryFabricPlanStatus(
    Guid PlanId, string Name, int PolicyCount, int RtoTargetMinutes, int MaxParallelAgents,
    bool Enabled, DateTimeOffset NextRunAtUtc, DateTimeOffset? LastRunAtUtc,
    string Readiness, int ReadinessScore, long? LastMeasuredRtoMilliseconds,
    bool RtoMet, int SuccessfulTargets, int FailedTargets, int MissingPolicies);

public sealed record RecoveryFabricSummary(
    DateTimeOffset GeneratedAtUtc, int FleetRecoveryScore, string FleetReadiness,
    int EnabledPlans, int ReadyPlans, int RtoBreaches, int PlansWithoutEvidence,
    int TotalProtectedPolicies, IReadOnlyList<RecoveryFabricPlanStatus> Plans);

public sealed class RecoveryFabricService
{
    private readonly IResilienceStore _resilience;
    private readonly IControlPlaneStore _store;

    public RecoveryFabricService(IResilienceStore resilience, IControlPlaneStore store)
    {
        _resilience=resilience;
        _store=store;
    }

    public async Task<RecoveryFabricSummary> BuildAsync(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var plans=await _resilience.GetRecoveryPlansAsync(ct).ConfigureAwait(false);
        var runs=await _resilience.GetRecoveryRunsAsync(1000,ct).ConfigureAwait(false);
        var policies=await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var rows=new List<RecoveryFabricPlanStatus>();

        foreach(var plan in plans.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
        {
            var last=runs.Where(r=>r.PlanId==plan.PlanId && r.CompletedAtUtc is not null)
                .OrderByDescending(r=>r.CompletedAtUtc).FirstOrDefault();
            var missing=plan.PolicyIds.Count(id=>policies.All(p=>p.PolicyId!=id));
            var success=last?.Targets.Count(t=>t.Succeeded==true)??0;
            var failed=last?.Targets.Count(t=>t.Succeeded==false)??0;
            var rtoMs=Math.Max(0,last?.LongestRestoreMilliseconds??0);
            var rtoMet=last is not null && string.Equals(last.Status,"completed",StringComparison.OrdinalIgnoreCase)
                && failed==0 && rtoMs<=TimeSpan.FromMinutes(plan.RtoTargetMinutes).TotalMilliseconds;
            var evidenceFresh=last?.CompletedAtUtc is DateTimeOffset completed &&
                completed>=now.AddDays(-Math.Max(1,plan.IntervalDays));
            var coverage=plan.PolicyIds.Count==0?0d:100d*(plan.PolicyIds.Count-missing)/plan.PolicyIds.Count;
            var successPct=last is null?0d:(last.Targets.Count==0?0d:100d*success/last.Targets.Count);
            var score=(int)Math.Round(Math.Clamp(coverage*.30+successPct*.35+(rtoMet?25:0)+(evidenceFresh?10:0),0,100));
            var readiness=score>=90?"ready":score>=70?"attention":"not-ready";
            rows.Add(new(plan.PlanId,plan.Name,plan.PolicyIds.Count,plan.RtoTargetMinutes,plan.MaxParallelAgents,
                plan.Enabled,plan.NextRunAtUtc,plan.LastRunAtUtc,readiness,score,
                last is null?null:rtoMs,rtoMet,success,failed,missing));
        }

        var enabled=rows.Where(x=>x.Enabled).ToArray();
        var fleetScore=enabled.Length==0?0:(int)Math.Round(enabled.Average(x=>x.ReadinessScore));
        return new(now,fleetScore,fleetScore>=90?"ready":fleetScore>=70?"attention":"not-ready",
            enabled.Length,enabled.Count(x=>x.Readiness=="ready"),enabled.Count(x=>!x.RtoMet && x.LastMeasuredRtoMilliseconds is not null),
            enabled.Count(x=>x.LastMeasuredRtoMilliseconds is null),enabled.Sum(x=>x.PolicyCount),rows);
    }
}
