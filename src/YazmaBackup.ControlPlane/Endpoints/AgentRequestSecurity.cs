using System.Security.Cryptography;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AgentRequestSecurity
{
    internal static async Task<(Guid AgentId, AgentRecord Record)?> AuthenticateAsync(
        HttpContext http,
        IControlPlaneStore store,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);

        if (!Guid.TryParse(http.Request.Headers["X-YazmaBackup-Agent-Id"].FirstOrDefault(), out var agentId))
            return null;

        var token = http.Request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        if (token is null || !token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var record = await store.AuthenticateAsync(agentId, token[prefix.Length..], ct).ConfigureAwait(false);
        return record is null ? null : (agentId, record);
    }

    internal static bool ValidMachine(string? value) => ValidText(value, 128);

    internal static bool ValidText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    internal static bool ValidIdentifier(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static bool ValidOptionalPublicKey(string? pem) =>
        string.IsNullOrWhiteSpace(pem) || ValidPublicKey(pem);

    internal static bool ValidPublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 8192) return false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 3072;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static string[] SanitizeCapabilities(IReadOnlyList<string>? capabilities) => (capabilities ?? [])
        .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 64)
        .Select(x => x.Trim())
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<RepositoryCircuitTelemetry>? SanitizeRepositoryCircuits(IReadOnlyList<RepositoryCircuitTelemetry>? circuits)
    {
        if (circuits is null) return null;
        return circuits.Where(x => ValidIdentifier(x.RepositoryId, 128))
            .Take(128)
            .Select(x => new RepositoryCircuitTelemetry(
                x.RepositoryId,
                Math.Clamp(x.ConsecutiveFailures, 0, 100000),
                x.LastFailureAtUtc,
                x.OpenUntilUtc,
                string.IsNullOrWhiteSpace(x.LastError) ? null : x.LastError.Length <= 256 ? x.LastError : x.LastError[..256]))
            .ToArray();
    }

    internal static ProtectionTelemetryDto? SanitizeProtection(ProtectionTelemetryDto? protection)
    {
        if (protection is null) return null;
        var status = protection.Status?.Trim().ToLowerInvariant();
        if (status is not ("healthy" or "locked")) return null;
        var reason = string.IsNullOrWhiteSpace(protection.Reason) ? null : protection.Reason.Trim();
        if (reason?.Length > 256) reason = reason[..256];
        return new ProtectionTelemetryDto(status, reason, protection.TriggeredAtUtc, protection.IncidentId);
    }
}
