using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private static readonly TimeSpan ManagementLockoutDuration = TimeSpan.FromMinutes(15);

    private const int MaxFailedLogins = 5;

    public async Task EnsureBootstrapAdministratorAsync(string username, string displayName, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.ManagementUsers.Count > 0) return;
            var normalized = NormalizeUsername(username);
            PasswordSecurity.ValidateNewPassword(password);
            var material = PasswordSecurity.HashPassword(password);
            var now = DateTimeOffset.UtcNow;
            var user = new ManagementUserRecord(
                Guid.NewGuid(), normalized, NormalizeDisplayName(displayName), material.HashBase64, material.SaltBase64,
                material.Iterations, [ManagementRoles.Administrator], true, true, now, null, 0, null);
            var nextState = CloneState();
            nextState.ManagementUsers[user.UserId] = user;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> AuthenticateManagementUserAsync(string username, string password, CancellationToken ct)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException)
        {
            PasswordSecurity.PerformDummyVerification(password);
            return null;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.ManagementUsers.Values.FirstOrDefault(u => string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                PasswordSecurity.PerformDummyVerification(password);
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (!current.Enabled || current.LockedUntilUtc is not null && current.LockedUntilUtc > now)
            {
                PasswordSecurity.PerformDummyVerification(password);
                return null;
            }

            if (!PasswordSecurity.Verify(password, current))
            {
                var failures = current.FailedLoginCount + 1;
                var lockedUntil = failures >= MaxFailedLogins ? now.Add(ManagementLockoutDuration) : current.LockedUntilUtc;
                if (failures >= MaxFailedLogins) failures = 0;
                var nextState = CloneState();
                nextState.ManagementUsers[current.UserId] = current with { FailedLoginCount = failures, LockedUntilUtc = lockedUntil };
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return null;
            }

            var authenticated = current with { LastLoginAtUtc = now, FailedLoginCount = 0, LockedUntilUtc = null };
            var successState = CloneState();
            successState.ManagementUsers[current.UserId] = authenticated;
            await CommitUnsafeAsync(successState, ct).ConfigureAwait(false);
            return authenticated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagementUserRecord>> GetManagementUsersAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ManagementUsers.Values.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord> CreateManagementUserAsync(string username, string displayName, string password, IReadOnlyList<string> roles, bool mustChangePassword, CancellationToken ct)
    {
        var normalized = NormalizeUsername(username);
        var normalizedRoles = NormalizeRoles(roles);
        PasswordSecurity.ValidateNewPassword(password);
        var material = PasswordSecurity.HashPassword(password);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.ManagementUsers.Values.Any(u => string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Management username already exists.");
            var now = DateTimeOffset.UtcNow;
            var user = new ManagementUserRecord(Guid.NewGuid(), normalized, NormalizeDisplayName(displayName), material.HashBase64, material.SaltBase64,
                material.Iterations, normalizedRoles, true, mustChangePassword, now, null, 0, null);
            var nextState = CloneState();
            nextState.ManagementUsers[user.UserId] = user;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return user;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> SetManagementUserEnabledAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current)) return null;
            if (!enabled && current.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
            {
                var enabledAdmins = _state.ManagementUsers.Values.Count(u => u.Enabled && u.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase));
                if (enabledAdmins <= 1) throw new InvalidOperationException("The last enabled administrator cannot be disabled.");
            }
            var updated = current with { Enabled = enabled, FailedLoginCount = 0, LockedUntilUtc = enabled ? null : current.LockedUntilUtc };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ChangeManagementPasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        PasswordSecurity.ValidateNewPassword(newPassword);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current) || !current.Enabled) return false;
            if (!PasswordSecurity.Verify(currentPassword, current)) return false;
            var material = PasswordSecurity.HashPassword(newPassword);
            var updated = current with
            {
                PasswordHashBase64 = material.HashBase64,
                PasswordSaltBase64 = material.SaltBase64,
                PasswordIterations = material.Iterations,
                MustChangePassword = false,
                FailedLoginCount = 0,
                LockedUntilUtc = null
            };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ResetManagementPasswordAfterBreakGlassAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        PasswordSecurity.ValidateNewPassword(newPassword);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(userId, out var current) || !current.Enabled || !current.MustChangePassword) return false;
            var material = PasswordSecurity.HashPassword(newPassword);
            var updated = current with
            {
                PasswordHashBase64 = material.HashBase64,
                PasswordSaltBase64 = material.SaltBase64,
                PasswordIterations = material.Iterations,
                MustChangePassword = false,
                FailedLoginCount = 0,
                LockedUntilUtc = null
            };
            var nextState = CloneState();
            nextState.ManagementUsers[userId] = updated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<(ManagementApiTokenRecord Record, string PlaintextToken)> CreateManagementApiTokenAsync(string name, IReadOnlyList<string> roles, TimeSpan validity, CancellationToken ct)
    {
        var normalizedName = NormalizeTokenName(name);
        var normalizedRoles = NormalizeRoles(roles);
        if (validity < TimeSpan.FromMinutes(5) || validity > TimeSpan.FromDays(90))
            throw new ArgumentOutOfRangeException(nameof(validity), "API token validity must be between 5 minutes and 90 days.");

        var plaintext = "ybmt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var record = new ManagementApiTokenRecord(Guid.NewGuid(), normalizedName, HashToken(plaintext), normalizedRoles, now, now.Add(validity), null, false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextState = CloneState();
            foreach (var expired in nextState.ManagementApiTokens.Values.Where(t => t.ExpiresAtUtc <= now || t.Revoked).Select(t => t.TokenId).ToArray())
                nextState.ManagementApiTokens.Remove(expired);
            nextState.ManagementApiTokens[record.TokenId] = record;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return (record, plaintext);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementApiTokenRecord?> AuthenticateManagementApiTokenAsync(string plaintextToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken) || !plaintextToken.StartsWith("ybmt_", StringComparison.Ordinal) || plaintextToken.Length != 69)
            return null;
        var tokenHash = HashToken(plaintextToken);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.ManagementApiTokens.Values.FirstOrDefault(t => !t.Revoked && t.ExpiresAtUtc > now && FixedTimeEquals(t.TokenHash, tokenHash));
            if (current is null) return null;
            if (current.LastUsedAtUtc is null || now - current.LastUsedAtUtc > TimeSpan.FromMinutes(1))
            {
                var updated = current with { LastUsedAtUtc = now };
                var nextState = CloneState();
                nextState.ManagementApiTokens[current.TokenId] = updated;
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return updated;
            }
            return current;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagementApiTokenRecord>> GetManagementApiTokensAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ManagementApiTokens.Values.OrderByDescending(t => t.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeManagementApiTokenAsync(Guid tokenId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementApiTokens.TryGetValue(tokenId, out var current)) return false;
            if (current.Revoked) return true;
            var nextState = CloneState();
            nextState.ManagementApiTokens[tokenId] = current with { Revoked = true };
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> CreateBreakGlassRecoveryCodesAsync(Guid administratorUserId, int count, TimeSpan validity, CancellationToken ct)
    {
        if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
        if (validity < TimeSpan.FromHours(1) || validity > TimeSpan.FromDays(90)) throw new ArgumentOutOfRangeException(nameof(validity));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.ManagementUsers.TryGetValue(administratorUserId, out var admin) || !admin.Enabled || !admin.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Break-glass codes can only be issued for an enabled administrator.");

            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(validity);
            var plaintextCodes = new List<string>(count);
            var nextState = CloneState();
            foreach (var stale in nextState.BreakGlassRecoveryCodes.Values.Where(c => c.UserId == administratorUserId && c.UsedAtUtc is null).Select(c => c.CodeId).ToArray())
                nextState.BreakGlassRecoveryCodes.Remove(stale);
            for (var i = 0; i < count; i++)
            {
                var code = "YBRC-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                plaintextCodes.Add(code);
                var record = new BreakGlassRecoveryCodeRecord(Guid.NewGuid(), administratorUserId, HashToken(code), now, expires, null);
                nextState.BreakGlassRecoveryCodes[record.CodeId] = record;
            }
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return plaintextCodes;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord?> RedeemBreakGlassRecoveryCodeAsync(string username, string recoveryCode, CancellationToken ct)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrWhiteSpace(recoveryCode) || recoveryCode.Length > 64) return null;
        var codeHash = HashToken(recoveryCode.Trim().ToUpperInvariant());
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var user = _state.ManagementUsers.Values.FirstOrDefault(u => u.Enabled && string.Equals(u.Username, normalized, StringComparison.OrdinalIgnoreCase));
            if (user is null || !user.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase)) return null;
            var code = _state.BreakGlassRecoveryCodes.Values.FirstOrDefault(c => c.UserId == user.UserId && c.UsedAtUtc is null && c.ExpiresAtUtc > now && FixedTimeEquals(c.CodeHash, codeHash));
            if (code is null) return null;
            var nextState = CloneState();
            nextState.BreakGlassRecoveryCodes[code.CodeId] = code with { UsedAtUtc = now };
            var authenticated = user with { LastLoginAtUtc = now, FailedLoginCount = 0, LockedUntilUtc = null, MustChangePassword = true };
            nextState.ManagementUsers[user.UserId] = authenticated;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return authenticated;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagementUserRecord> FindOrProvisionExternalUserAsync(string issuer, string subject, string username, string displayName, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var normalizedIssuer = NormalizeExternalIdentityPart(issuer, 512, nameof(issuer));
        var normalizedSubject = NormalizeExternalIdentityPart(subject, 256, nameof(subject));
        var normalizedRoles = NormalizeRoles(roles);
        if (normalizedRoles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("OIDC identities cannot be auto-provisioned with the local break-glass administrator role.");
        var proposedUsername = NormalizeUsername(username);
        var safeDisplayName = NormalizeDisplayName(displayName);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var identity = _state.ExternalIdentities.Values.FirstOrDefault(i => string.Equals(i.Issuer, normalizedIssuer, StringComparison.Ordinal) && string.Equals(i.Subject, normalizedSubject, StringComparison.Ordinal));
            if (identity is not null && _state.ManagementUsers.TryGetValue(identity.UserId, out var existing))
            {
                if (!existing.Enabled) throw new UnauthorizedAccessException("The externally mapped user is disabled.");
                var nextState = CloneState();
                nextState.ExternalIdentities[identity.IdentityId] = identity with { LastLoginAtUtc = now };
                var updated = existing with { LastLoginAtUtc = now, Roles = normalizedRoles, DisplayName = safeDisplayName };
                nextState.ManagementUsers[existing.UserId] = updated;
                await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
                return updated;
            }

            var finalUsername = proposedUsername;
            if (_state.ManagementUsers.Values.Any(u => string.Equals(u.Username, proposedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIssuer + "\n" + normalizedSubject))).ToLowerInvariant()[..12];
                finalUsername = "oidc-" + suffix;
            }
            var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            var material = PasswordSecurity.HashPassword(randomPassword);
            var user = new ManagementUserRecord(Guid.NewGuid(), finalUsername, safeDisplayName, material.HashBase64, material.SaltBase64, material.Iterations, normalizedRoles, true, false, now, now, 0, null);
            var link = new ExternalIdentityRecord(Guid.NewGuid(), user.UserId, normalizedIssuer, normalizedSubject, now, now);
            var createdState = CloneState();
            createdState.ManagementUsers[user.UserId] = user;
            createdState.ExternalIdentities[link.IdentityId] = link;
            await CommitUnsafeAsync(createdState, ct).ConfigureAwait(false);
            return user;
        }
        finally { _gate.Release(); }
    }

    private static string NormalizeUsername(string username)
    {
        var value = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 3 or > 64 || !value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Management username is invalid.", nameof(username));
        return value;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        var value = (displayName ?? string.Empty).Trim();
        if (value.Length is < 2 or > 128) throw new ArgumentException("Display name is invalid.", nameof(displayName));
        return value;
    }

    private static string[] NormalizeRoles(IReadOnlyList<string> roles)
    {
        var normalized = (roles ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Any(r => !ManagementRoles.All.Contains(r)))
            throw new ArgumentException("At least one valid management role is required.", nameof(roles));
        return normalized;
    }

    private static string NormalizeTokenName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length is < 3 or > 64 || value.Any(char.IsControl)) throw new ArgumentException("API token name is invalid.", nameof(name));
        return value;
    }

    private static string NormalizeExternalIdentityPart(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || normalized.Any(char.IsControl)) throw new ArgumentException("External identity value is invalid.", parameterName);
        return normalized;
    }
}
