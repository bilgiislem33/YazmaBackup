namespace YazmaBackup.ControlPlane;

public sealed record FleetAutopilotPlanItem(
    string Id, string Severity, string Category, Guid? AgentId, string? MachineName,
    string Problem, string ProposedAction, string AutomationLevel, bool RequiresApproval);

public sealed record FleetAutopilotSummary(
    DateTimeOffset GeneratedAtUtc, int RestoreReadinessScore, string RestoreReadiness,
    int RecoveryRuns30d, int SuccessfulRecoveryRuns30d, int FailedRecoveryRuns30d,
    int SlaRiskCount, int AutoDiagnosisCandidates, int ApprovalRequired,
    IReadOnlyList<FleetAutopilotPlanItem> Plan);

public sealed class FleetAutopilotService
{
    private readonly FleetBackupIntelligenceService _intelligence;
    private readonly IResilienceStore _resilience;

    public FleetAutopilotService(FleetBackupIntelligenceService intelligence, IResilienceStore resilience)
    {
        _intelligence = intelligence;
        _resilience = resilience;
    }

    public async Task<FleetAutopilotSummary> BuildAsync(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var intelligence=await _intelligence.BuildAsync(ct).ConfigureAwait(false);
        var runs=await _resilience.GetRecoveryRunsAsync(500,ct).ConfigureAwait(false);
        var recent=runs.Where(r=>r.StartedAtUtc>=now.AddDays(-30)).ToArray();
        var successful=recent.Count(r=>r.CompletedAtUtc is not null &&
            string.Equals(r.Status,"completed",StringComparison.OrdinalIgnoreCase) &&
            r.Targets.All(t=>t.Succeeded==true));
        var failed=recent.Count(r=>r.CompletedAtUtc is not null &&
            (!string.Equals(r.Status,"completed",StringComparison.OrdinalIgnoreCase) || r.Targets.Any(t=>t.Succeeded==false)));

        var restoreSuccess=recent.Length==0?70d:100d*successful/recent.Length;
        var evidenceFreshness=recent.Length==0?0d:Math.Clamp(100d-(now-recent.Max(r=>r.StartedAtUtc)).TotalDays*2.5,0,100);
        var readiness=(int)Math.Round(Math.Clamp(restoreSuccess*.70+evidenceFreshness*.30,0,100));

        var plan=intelligence.Risks.Select(r=>new FleetAutopilotPlanItem(
            r.Id,r.Severity,r.Category,r.AgentId,r.MachineName,r.Title,r.RecommendedAction,
            r.RequiresApproval?"approval-required":r.CanAutoDiagnose?"auto-diagnose":"operator-plan",
            r.RequiresApproval)).Take(200).ToArray();

        var slaRisks=intelligence.Risks.Count(r=>r.Category is "backup-failed" or "policy-overdue" or "repository-capacity");
        return new(now,readiness,readiness>=90?"ready":readiness>=70?"attention":"not-ready",
            recent.Length,successful,failed,slaRisks,
            plan.Count(x=>x.AutomationLevel=="auto-diagnose"),
            plan.Count(x=>x.RequiresApproval),plan);
    }
}
