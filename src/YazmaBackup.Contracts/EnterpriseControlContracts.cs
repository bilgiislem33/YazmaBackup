using YazmaBackup.Domain;

namespace YazmaBackup.Contracts;

public sealed record CreateManagementApiTokenRequest(string Name, IReadOnlyList<string> Roles, int ValidForHours = 24);
public sealed record CreateManagementApiTokenResponse(Guid TokenId, string Token, string Name, IReadOnlyList<string> Roles, DateTimeOffset ExpiresAtUtc);
public sealed record ManagementApiTokenDto(Guid TokenId, string Name, IReadOnlyList<string> Roles, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, bool Revoked);
public sealed record RevokeManagementApiTokenRequest(bool Revoke = true);

public sealed record CreateBreakGlassCodesRequest(int Count = 8, int ValidForDays = 30);
public sealed record CreateBreakGlassCodesResponse(DateTimeOffset ExpiresAtUtc, IReadOnlyList<string> Codes);
public sealed record BreakGlassLoginRequest(string Username, string RecoveryCode);

public sealed record AlarmDto(Guid AlarmId, string Severity, string Category, string Title, string? Details, string Status, Guid? AgentId, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? AcknowledgedAtUtc, string? AcknowledgedBy, DateTimeOffset? ResolvedAtUtc, string? AssignedTo, string? OperatorNote, DateTimeOffset? DueAtUtc, DateTimeOffset? WorkflowUpdatedAtUtc);
public sealed record AcknowledgeAlarmRequest(bool Acknowledge = true);
public sealed record UpdateAlarmWorkflowRequest(string? AssignedTo = null, string? Note = null, DateTimeOffset? DueAtUtc = null);
public sealed record BulkAlarmActionRequest(IReadOnlyList<Guid> AlarmIds, string Action, string? AssignedTo = null, string? Note = null, DateTimeOffset? DueAtUtc = null);
public sealed record AlarmSummaryDto(int OpenCritical, int OpenWarning, int OpenInfo, int Acknowledged, int Resolved, DateTimeOffset GeneratedAtUtc);

public sealed record ClusterStatusDto(string NodeId, bool SchedulerLeader, string? LeaseOwner, long? LeaseEpoch, DateTimeOffset? LeaseExpiresAtUtc, DateTimeOffset GeneratedAtUtc);
