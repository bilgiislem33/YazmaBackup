using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<AlarmRecord> UpsertAlarmAsync(string fingerprint, string severity, string category, string title, string? details, Guid? agentId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        fingerprint = NormalizeAlarmText(fingerprint, 256, nameof(fingerprint));
        category = NormalizeAlarmText(category, 64, nameof(category));
        title = NormalizeAlarmText(title, 160, nameof(title));
        if (!AlarmSeverity.All.Contains(severity)) throw new ArgumentException("Alarm severity is invalid.", nameof(severity));
        details = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (details?.Length > 1024) details = details[..1024];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _state.Alarms.Values.FirstOrDefault(a => string.Equals(a.Fingerprint, fingerprint, StringComparison.Ordinal));
            var normalizedSeverity = severity.ToLowerInvariant();
            static DateTimeOffset DefaultDue(string sev, DateTimeOffset now) => sev == AlarmSeverity.Critical ? now.AddMinutes(15) : sev == AlarmSeverity.Warning ? now.AddHours(4) : now.AddHours(24);
            var record = existing is null
                ? new AlarmRecord(Guid.NewGuid(), fingerprint, normalizedSeverity, category, title, details, AlarmStatus.Open, agentId, nowUtc, nowUtc, null, null, null, null, null, DefaultDue(normalizedSeverity, nowUtc), nowUtc)
                : existing with { Severity = normalizedSeverity, Category = category, Title = title, Details = details, AgentId = agentId, LastSeenAtUtc = nowUtc, Status = existing.Status == AlarmStatus.Resolved ? AlarmStatus.Open : existing.Status, ResolvedAtUtc = null, DueAtUtc = existing.Status == AlarmStatus.Resolved ? DefaultDue(normalizedSeverity, nowUtc) : existing.DueAtUtc ?? DefaultDue(normalizedSeverity, nowUtc), WorkflowUpdatedAtUtc = existing.WorkflowUpdatedAtUtc ?? nowUtc };
            var nextState = CloneState();
            nextState.Alarms[record.AlarmId] = record;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ResolveAlarmAsync(string fingerprint, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.Alarms.Values.FirstOrDefault(a => string.Equals(a.Fingerprint, fingerprint, StringComparison.Ordinal) && a.Status != AlarmStatus.Resolved);
            if (current is null) return false;
            var nextState = CloneState();
            nextState.Alarms[current.AlarmId] = current with { Status = AlarmStatus.Resolved, ResolvedAtUtc = nowUtc, LastSeenAtUtc = nowUtc, WorkflowUpdatedAtUtc = nowUtc };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> AcknowledgeAlarmAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current) || current.Status == AlarmStatus.Resolved) return null;
            var updated = current with { Status = AlarmStatus.Acknowledged, AcknowledgedAtUtc = nowUtc, AcknowledgedBy = actor, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState();
            nextState.Alarms[alarmId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> UpdateAlarmWorkflowAsync(Guid alarmId, string? assignedTo, string? note, DateTimeOffset? dueAtUtc, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        assignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : NormalizeAlarmText(assignedTo, 128, nameof(assignedTo));
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 2000) note = note[..2000];
        if (dueAtUtc is not null && (dueAtUtc.Value < nowUtc.AddDays(-1) || dueAtUtc.Value > nowUtc.AddDays(365))) throw new ArgumentOutOfRangeException(nameof(dueAtUtc));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current)) return null;
            var updated = current with { AssignedTo = assignedTo, OperatorNote = note, DueAtUtc = dueAtUtc ?? current.DueAtUtc, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState(); nextState.Alarms[alarmId] = updated; await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<AlarmRecord?> ResolveAlarmByIdAsync(Guid alarmId, string actor, DateTimeOffset nowUtc, CancellationToken ct)
    {
        actor = NormalizeAlarmText(actor, 128, nameof(actor));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Alarms.TryGetValue(alarmId, out var current)) return null;
            var updated = current with { Status = AlarmStatus.Resolved, ResolvedAtUtc = nowUtc, WorkflowUpdatedAtUtc = nowUtc };
            var nextState = CloneState(); nextState.Alarms[alarmId] = updated; await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false); return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AlarmRecord>> GetAlarmsAsync(int limit, bool includeResolved, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 2000);
        if (_directSqlReads) return await _postgresql!.GetAlarmsAsync(limit, includeResolved, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _state.Alarms.Values.Where(a => includeResolved || a.Status != AlarmStatus.Resolved)
                .OrderByDescending(a => a.Status != AlarmStatus.Resolved)
                .ThenByDescending(a => a.Severity == AlarmSeverity.Critical)
                .ThenByDescending(a => a.LastSeenAtUtc).Take(limit).ToArray();
        }
        finally { _gate.Release(); }
    }

    private static string NormalizeAlarmText(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || normalized.Any(char.IsControl)) throw new ArgumentException("Alarm value is invalid.", parameterName);
        return normalized;
    }
}
