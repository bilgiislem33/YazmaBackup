using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<(AgentRecord Agent, string AccessToken)> EnrollAgentAsync(
        string enrollmentToken,
        string machineName,
        string operatingSystem,
        string agentVersion,
        IReadOnlyList<string> capabilities,
        string? keyExchangePublicKeyPem,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(enrollmentToken))
            throw new UnauthorizedAccessException("Enrollment token is required.");

        var enrollmentHash = HashToken(enrollmentToken);
        var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var grant = _state.EnrollmentGrants.Values.FirstOrDefault(g => FixedTimeEquals(g.TokenHash, enrollmentHash));
            if (grant is null || grant.ExpiresAtUtc <= now || grant.RemainingUses <= 0)
                throw new UnauthorizedAccessException("Enrollment token is invalid or expired.");

            var record = new AgentRecord(
                Guid.NewGuid(), machineName, operatingSystem, HashToken(accessToken), now, now, agentVersion,
                capabilities.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                keyExchangePublicKeyPem);

            var nextState = CloneState();
            nextState.Agents[record.AgentId] = record;
            if (grant.RemainingUses == 1)
                nextState.EnrollmentGrants.Remove(grant.GrantId);
            else
                nextState.EnrollmentGrants[grant.GrantId] = grant with { RemainingUses = grant.RemainingUses - 1 };

            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (record, accessToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<(EnrollmentGrant Grant, string EnrollmentToken)> CreateEnrollmentGrantAsync(TimeSpan validity, int maxUses, CancellationToken ct)
    {
        if (validity < TimeSpan.FromMinutes(1) || validity > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(validity), "Enrollment token validity must be between 1 minute and 24 hours.");
        if (maxUses is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(maxUses), "Enrollment token max uses must be between 1 and 5000.");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var grant = new EnrollmentGrant(Guid.NewGuid(), HashToken(token), now, now.Add(validity), maxUses);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            foreach (var expired in nextState.EnrollmentGrants.Values
                         .Where(g => g.ExpiresAtUtc <= now || g.RemainingUses <= 0)
                         .Select(g => g.GrantId)
                         .ToArray())
                nextState.EnrollmentGrants.Remove(expired);
            nextState.EnrollmentGrants[grant.GrantId] = grant;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (grant, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentRecord?> AuthenticateAsync(Guid agentId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var agent)) return null;
            return FixedTimeEquals(agent.TokenHash, HashToken(token)) ? agent : null;
        }
        finally { _gate.Release(); }
    }

    public async Task TouchAsync(Guid agentId, string machineName, string operatingSystem, string agentVersion, IReadOnlyList<string> capabilities, string? keyExchangePublicKeyPem, ProtectionTelemetryDto? protection, IReadOnlyList<RepositoryCircuitTelemetry>? repositoryCircuits, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var existing)) return;
            var nextState = CloneState();
            nextState.Agents[agentId] = existing with
            {
                MachineName = machineName,
                OperatingSystem = operatingSystem,
                AgentVersion = agentVersion,
                Capabilities = capabilities.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                KeyExchangePublicKeyPem = string.IsNullOrWhiteSpace(keyExchangePublicKeyPem) ? existing.KeyExchangePublicKeyPem : keyExchangePublicKeyPem,
                ProtectionStatus = protection?.Status ?? existing.ProtectionStatus,
                ProtectionReason = protection is null ? existing.ProtectionReason : protection.Reason,
                ProtectionTriggeredAtUtc = protection is null ? existing.ProtectionTriggeredAtUtc : protection.TriggeredAtUtc,
                ProtectionIncidentId = protection is null ? existing.ProtectionIncidentId : protection.IncidentId,
                RepositoryCircuits = repositoryCircuits is null ? existing.RepositoryCircuits : repositoryCircuits.Take(128).ToArray(),
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentRecord>> GetAgentsAsync(CancellationToken ct)
    {
        if (_directSqlReads) return await _postgresql!.GetAgentsAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.Agents.Values.OrderBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<AgentRecord?> SetAgentAssignedUserAsync(Guid agentId, string? assignedUser, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(assignedUser) ? null : assignedUser.Trim();
        if (normalized is not null)
        {
            if (normalized.Length > 128 || normalized.Any(char.IsControl))
                throw new ArgumentException("Assigned user must be <= 128 characters and contain no control characters.", nameof(assignedUser));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Agents.TryGetValue(agentId, out var existing)) return null;
            var updated = existing with { AssignedUser = normalized };
            var nextState = CloneState();
            nextState.Agents[agentId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }
}
