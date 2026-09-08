using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private const int MaxAuditEvents = 50_000;

    public async Task AppendAuditEventAsync(AuditEventRecord auditEvent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            nextState.AuditEvents[auditEvent.EventId] = auditEvent;
            if (nextState.AuditEvents.Count > MaxAuditEvents)
            {
                foreach (var id in nextState.AuditEvents.Values.OrderBy(x => x.OccurredAtUtc).Take(nextState.AuditEvents.Count - MaxAuditEvents).Select(x => x.EventId).ToArray())
                    nextState.AuditEvents.Remove(id);
            }
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEventRecord>> GetAuditEventsAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        if (_directSqlReads) return await _postgresql!.GetAuditEventsAsync(limit, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.AuditEvents.Values.OrderByDescending(a => a.OccurredAtUtc).Take(limit).ToArray(); }
        finally { _gate.Release(); }
    }
}
