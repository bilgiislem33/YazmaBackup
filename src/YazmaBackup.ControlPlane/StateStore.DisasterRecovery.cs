using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<IReadOnlyList<DisasterRecoverySessionRecord>> GetDisasterRecoverySessionsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.DisasterRecoverySessions.Values.OrderByDescending(x=>x.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> CreateDisasterRecoverySessionAsync(
        string name, IReadOnlyList<DisasterRecoverySessionStepRecord> steps, CancellationToken ct)
    {
        name=(name??string.Empty).Trim();
        if(name.Length is <3 or >160) throw new ArgumentException("DR session name must be 3-160 characters.");
        if(steps.Count==0 || steps.Any(x=>x.Status=="blocked"))
            throw new InvalidOperationException("DR session cannot start from an empty or cyclic/blocked Recovery DAG.");
        var now=DateTimeOffset.UtcNow;
        var record=new DisasterRecoverySessionRecord(Guid.NewGuid(),name,"active",now,now,null,1,
            steps.Select(x=>x with { Status="pending", ApprovedAtUtc=null, VerifiedAtUtc=null, VerificationNote=null }).ToArray());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next=CloneState();
            next.DisasterRecoverySessions[record.SessionId]=record;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> ApproveDisasterRecoveryStepAsync(Guid sessionId,int order,CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status!="active") throw new InvalidOperationException("DR session is not active.");
            if(order!=session.CurrentOrder) throw new InvalidOperationException("Only the current dependency-safe DR step can be approved.");
            var step=session.Steps.FirstOrDefault(x=>x.Order==order) ?? throw new KeyNotFoundException("DR step not found.");
            if(step.Status=="verified") return session;
            var now=DateTimeOffset.UtcNow;
            var steps=session.Steps.Select(x=>x.Order==order?x with { Status="approved",ApprovedAtUtc=now }:x).ToArray();
            var updated=session with { Steps=steps };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> VerifyDisasterRecoveryStepAsync(Guid sessionId,int order,string verificationNote,CancellationToken ct)
    {
        verificationNote=(verificationNote??string.Empty).Trim();
        if(verificationNote.Length is <3 or >1000) throw new ArgumentException("Verification note must be 3-1000 characters.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status!="active" || order!=session.CurrentOrder) throw new InvalidOperationException("Only the current active DR step can be verified.");
            var step=session.Steps.FirstOrDefault(x=>x.Order==order) ?? throw new KeyNotFoundException("DR step not found.");
            if(step.Status!="approved") throw new InvalidOperationException("DR step requires explicit approval before verification.");
            var now=DateTimeOffset.UtcNow;
            var steps=session.Steps.Select(x=>x.Order==order?x with { Status="verified",VerifiedAtUtc=now,VerificationNote=verificationNote }:x).ToArray();
            var nextOrder=steps.Where(x=>x.Status!="verified").Select(x=>x.Order).DefaultIfEmpty(0).Min();
            var completed=nextOrder==0;
            var updated=session with { Steps=steps,CurrentOrder=nextOrder,Status=completed?"completed":"active",CompletedAtUtc=completed?now:null };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<DisasterRecoverySessionRecord> CancelDisasterRecoverySessionAsync(Guid sessionId,CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.DisasterRecoverySessions.TryGetValue(sessionId,out var session)) throw new KeyNotFoundException("DR session not found.");
            if(session.Status=="completed") return session;
            var updated=session with { Status="cancelled",CompletedAtUtc=DateTimeOffset.UtcNow };
            var next=CloneState(); next.DisasterRecoverySessions[sessionId]=updated;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }
}
