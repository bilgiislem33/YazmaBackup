using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record ClosedLoopCase(
    string CaseId, string Severity, string RiskType, Guid? AgentId, string? MachineName,
    string Title, string Stage, int ConfidencePct, string Evidence,
    string RecommendedAction, bool SafeDiagnosisAvailable, bool RequiresApproval,
    Guid? RemediationRunId, string? RemediationState);

public sealed record ClosedLoopProtectionSummary(
    DateTimeOffset GeneratedAtUtc, int OpenCases, int Diagnosing, int AwaitingApproval,
    int Verifying, int Closed, IReadOnlyList<ClosedLoopCase> Cases);

public sealed class ClosedLoopProtectionService
{
    private readonly PredictiveProtectionService _predictive;
    private readonly IControlPlaneStore _store;
    private readonly AutonomousOrchestrationStore _orchestration;

    public ClosedLoopProtectionService(
        PredictiveProtectionService predictive,
        IControlPlaneStore store,
        AutonomousOrchestrationStore orchestration)
    {
        _predictive=predictive;
        _store=store;
        _orchestration=orchestration;
    }

    public async Task<ClosedLoopProtectionSummary> BuildAsync(CancellationToken ct)
    {
        var predictive=await _predictive.BuildAsync(ct).ConfigureAwait(false);
        var runs=await _orchestration.GetRemediationsAsync(ct).ConfigureAwait(false);
        var policies=await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var cases=new List<ClosedLoopCase>();

        foreach(var risk in predictive.Predictions)
        {
            var policyId=TryPolicyId(risk.Id);
            var policy=policyId is Guid pid ? policies.FirstOrDefault(p=>p.PolicyId==pid) : null;
            var related=runs.Where(r=>r.AgentId==risk.AgentId &&
                    (policy is null || string.Equals(r.RepositoryId,policy.RepositoryId,StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(r=>r.UpdatedAtUtc).FirstOrDefault();
            var safe=policy is not null && risk.RiskType is ("sla-breach" or "restore-evidence");
            var stage=related?.State switch
            {
                "queued-diagnosis" or "diagnosing" => "diagnosing",
                "awaiting-approval" => "awaiting-approval",
                "approved" or "applying" => "applying",
                "verifying" => "verifying",
                "completed" => "verified",
                "failed" => "needs-attention",
                _ => "detected"
            };
            cases.Add(new(risk.Id,risk.Severity,risk.RiskType,risk.AgentId,risk.MachineName,
                risk.Title,stage,risk.ConfidencePct,risk.Prediction,risk.RecommendedAction,
                safe,risk.RequiresApproval,related?.RunId,related?.State));
        }

        return new(DateTimeOffset.UtcNow,
            cases.Count(x=>x.Stage is "detected" or "needs-attention"),
            cases.Count(x=>x.Stage=="diagnosing"),
            cases.Count(x=>x.Stage=="awaiting-approval"),
            cases.Count(x=>x.Stage is "verifying" or "applying"),
            cases.Count(x=>x.Stage=="verified"),
            cases.OrderBy(x=>x.Severity=="critical"?0:1).ThenByDescending(x=>x.ConfidencePct).Take(300).ToArray());
    }

    public async Task<AutonomousRemediationRun?> StartSafeDiagnosisAsync(string caseId, CancellationToken ct)
    {
        var predictive=await _predictive.BuildAsync(ct).ConfigureAwait(false);
        var risk=predictive.Predictions.FirstOrDefault(x=>string.Equals(x.Id,caseId,StringComparison.Ordinal));
        if(risk is null || risk.AgentId is null) return null;
        if(risk.RiskType is not ("sla-breach" or "restore-evidence")) return null;

        var policyId=TryPolicyId(risk.Id);
        if(policyId is not Guid pid) return null;
        var policies=await _store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var policy=policies.FirstOrDefault(x=>x.PolicyId==pid && x.AgentId==risk.AgentId.Value);
        if(policy is null) return null;

        var existing=(await _orchestration.GetRemediationsAsync(ct).ConfigureAwait(false))
            .Where(x=>x.AgentId==policy.AgentId &&
                string.Equals(x.RepositoryId,policy.RepositoryId,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ActionId,"diagnose-only",StringComparison.Ordinal) &&
                x.State is "queued-diagnosis" or "diagnosing")
            .OrderByDescending(x=>x.UpdatedAtUtc).FirstOrDefault();
        if(existing is not null) return existing;

        return await _orchestration.CreateRemediationAsync(new CreateAutonomousRemediationRequest(
            policy.AgentId,policy.SourcePath,policy.RepositoryRoot,policy.RepositoryId,"diagnose-only"),ct).ConfigureAwait(false);
    }

    private static Guid? TryPolicyId(string riskId)
    {
        var index=riskId.IndexOf(':');
        if(index<0 || index==riskId.Length-1) return null;
        return Guid.TryParseExact(riskId[(index+1)..],"N",out var id)?id:null;
    }
}
