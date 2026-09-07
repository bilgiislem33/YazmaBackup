using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record RecoveryGraphNode(
    string ServiceId, string Name, string Criticality, int RtoTargetMinutes,
    int ReadinessScore, string Readiness, IReadOnlyList<string> DependsOn);

public sealed record RecoveryGraphStep(
    int Order, string ServiceId, string ServiceName, string Criticality,
    int RtoTargetMinutes, IReadOnlyList<string> RequiredDependencies,
    string Gate, bool Blocked);

public sealed record BusinessServiceGraphSummary(
    DateTimeOffset GeneratedAtUtc, int Nodes, int Dependencies, int HardDependencies,
    bool HasCycle, IReadOnlyList<string> CycleServices,
    IReadOnlyList<RecoveryGraphNode> Graph,
    IReadOnlyList<RecoveryGraphStep> RecoveryDag);

public sealed class BusinessServiceGraphService
{
    private readonly BusinessContinuityService _continuity;
    private readonly IBusinessContinuityStore _store;

    public BusinessServiceGraphService(BusinessContinuityService continuity, IBusinessContinuityStore store)
    {
        _continuity=continuity; _store=store;
    }

    public async Task<BusinessServiceGraphSummary> BuildAsync(CancellationToken ct)
    {
        var continuity=await _continuity.BuildAsync(ct).ConfigureAwait(false);
        var deps=await _store.GetDependenciesAsync(ct).ConfigureAwait(false);
        var ids=continuity.Services.Select(x=>x.ServiceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid=deps.Where(d=>ids.Contains(d.ServiceId) && ids.Contains(d.DependsOnServiceId)).ToArray();

        var graph=continuity.Services.Select(s=>new RecoveryGraphNode(
            s.ServiceId,s.Name,s.Criticality,s.RtoTargetMinutes,s.ReadinessScore,s.Readiness,
            valid.Where(d=>string.Equals(d.ServiceId,s.ServiceId,StringComparison.OrdinalIgnoreCase))
                .Select(d=>d.DependsOnServiceId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();

        var indegree=ids.ToDictionary(x=>x,_=>0,StringComparer.OrdinalIgnoreCase);
        var outgoing=ids.ToDictionary(x=>x,_=>new List<string>(),StringComparer.OrdinalIgnoreCase);
        foreach(var d in valid.Where(x=>x.Required))
        {
            // dependsOn must be recovered before ServiceId.
            outgoing[d.DependsOnServiceId].Add(d.ServiceId);
            indegree[d.ServiceId]++;
        }

        var ready=new SortedSet<string>(Comparer<string>.Create((a,b)=>
        {
            var sa=continuity.Services.First(x=>string.Equals(x.ServiceId,a,StringComparison.OrdinalIgnoreCase));
            var sb=continuity.Services.First(x=>string.Equals(x.ServiceId,b,StringComparison.OrdinalIgnoreCase));
            var c=sa.Priority.CompareTo(sb.Priority);
            if(c!=0)return c;
            c=(sa.RtoTargetMinutes==0?int.MaxValue:sa.RtoTargetMinutes).CompareTo(sb.RtoTargetMinutes==0?int.MaxValue:sb.RtoTargetMinutes);
            return c!=0?c:StringComparer.OrdinalIgnoreCase.Compare(a,b);
        }));
        foreach(var kv in indegree.Where(x=>x.Value==0)) ready.Add(kv.Key);

        var ordered=new List<string>();
        while(ready.Count>0)
        {
            var id=ready.Min!; ready.Remove(id); ordered.Add(id);
            foreach(var child in outgoing[id])
            {
                indegree[child]--;
                if(indegree[child]==0) ready.Add(child);
            }
        }
        var cycles=ids.Where(id=>!ordered.Contains(id,StringComparer.OrdinalIgnoreCase)).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        // Cyclic services are never silently ordered; they remain blocked and require graph correction.
        var steps=ordered.Select((id,i)=>
        {
            var s=continuity.Services.First(x=>string.Equals(x.ServiceId,id,StringComparison.OrdinalIgnoreCase));
            var required=valid.Where(d=>d.Required && string.Equals(d.ServiceId,id,StringComparison.OrdinalIgnoreCase))
                .Select(d=>d.DependsOnServiceId).ToArray();
            return new RecoveryGraphStep(i+1,id,s.Name,s.Criticality,s.RtoTargetMinutes,required,
                s.Readiness=="ready"?"Recovery evidence gate passed":"Recovery evidence requires attention",false);
        }).Concat(cycles.Select((id,i)=>
        {
            var s=continuity.Services.First(x=>string.Equals(x.ServiceId,id,StringComparison.OrdinalIgnoreCase));
            var required=valid.Where(d=>d.Required && string.Equals(d.ServiceId,id,StringComparison.OrdinalIgnoreCase))
                .Select(d=>d.DependsOnServiceId).ToArray();
            return new RecoveryGraphStep(ordered.Count+i+1,id,s.Name,s.Criticality,s.RtoTargetMinutes,required,
                "BLOCKED: dependency cycle must be corrected before execution",true);
        })).ToArray();

        return new(DateTimeOffset.UtcNow,ids.Count,valid.Length,valid.Count(x=>x.Required),cycles.Length>0,cycles,graph,steps);
    }
}
