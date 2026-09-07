using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed class DisasterRecoveryExecutionService
{
    private readonly BusinessServiceGraphService _graph;
    private readonly IDisasterRecoverySessionStore _sessions;

    public DisasterRecoveryExecutionService(BusinessServiceGraphService graph, IDisasterRecoverySessionStore sessions)
    {
        _graph=graph; _sessions=sessions;
    }

    public Task<IReadOnlyList<DisasterRecoverySessionRecord>> GetAsync(CancellationToken ct) =>
        _sessions.GetDisasterRecoverySessionsAsync(ct);

    public async Task<DisasterRecoverySessionRecord> StartAsync(string name,CancellationToken ct)
    {
        var graph=await _graph.BuildAsync(ct).ConfigureAwait(false);
        if(graph.HasCycle) throw new InvalidOperationException("Recovery DAG contains a dependency cycle. Correct the graph before starting DR.");
        var steps=graph.RecoveryDag.Select(x=>new DisasterRecoverySessionStepRecord(
            x.Order,x.ServiceId,x.ServiceName,x.RequiredDependencies,
            x.Blocked?"blocked":"pending",null,null,null)).ToArray();
        return await _sessions.CreateDisasterRecoverySessionAsync(name,steps,ct).ConfigureAwait(false);
    }

    public Task<DisasterRecoverySessionRecord> ApproveAsync(Guid sessionId,int order,CancellationToken ct) =>
        _sessions.ApproveDisasterRecoveryStepAsync(sessionId,order,ct);

    public Task<DisasterRecoverySessionRecord> VerifyAsync(Guid sessionId,int order,string note,CancellationToken ct) =>
        _sessions.VerifyDisasterRecoveryStepAsync(sessionId,order,note,ct);

    public Task<DisasterRecoverySessionRecord> CancelAsync(Guid sessionId,CancellationToken ct) =>
        _sessions.CancelDisasterRecoverySessionAsync(sessionId,ct);
}
