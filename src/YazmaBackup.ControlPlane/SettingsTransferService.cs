using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record SettingsExportRequest(string Passphrase);
public sealed record SettingsImportRequest(string Passphrase, string BundleBase64);
public sealed record SettingsExportResponse(string FileName, string BundleBase64, DateTimeOffset CreatedAtUtc);
public sealed record SettingsImportResponse(
    int AssignedUsersApplied,
    int PoliciesCreated,
    int PoliciesSkipped,
    int AgentsMissing,
    int NasAgentsQueued,
    int NasAgentsSkipped);

public sealed class SettingsTransferService(
    IControlPlaneStore store,
    GlobalNasProfileStore nasProfiles,
    GlobalNasProfileService globalNas)
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxBundleBytes = 8 * 1024 * 1024;

    public async Task<SettingsExportResponse> ExportAsync(string passphrase, CancellationToken ct)
    {
        ValidatePassphrase(passphrase);

        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var nas = await nasProfiles.GetAsync(ct).ConfigureAwait(false);

        var agentById = agents.ToDictionary(x => x.AgentId);
        var payload = new SettingsPayload(
            SchemaVersion: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            GlobalNas: nas is null ? null : new ExportNasProfile(nas.RepositoryId, nas.RepositoryRoot, nas.Username, nas.Password),
            Agents: agents
                .OrderBy(x => x.MachineName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new ExportAgentSettings(x.MachineName, x.AssignedUser))
                .ToArray(),
            Policies: policies
                .Where(p => agentById.ContainsKey(p.AgentId))
                .OrderBy(p => agentById[p.AgentId].MachineName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ExportPolicy(
                    agentById[p.AgentId].MachineName,
                    p.Name,
                    p.SourcePath,
                    p.RepositoryRoot,
                    p.RepositoryId,
                    p.RequireSnapshot,
                    p.IntervalMinutes,
                    p.ActiveBytesPerSecond,
                    p.IdleBytesPerSecond,
                    p.UserIdleThresholdSeconds,
                    p.Retention,
                    p.Protection ?? new ProtectionPolicy(),
                    p.Enabled,
                    p.RestoreDrillIntervalDays,
                    p.RepositoryHealthIntervalHours))
                .ToArray());

        var clear = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var tag = new byte[TagBytes];
            var cipher = new byte[clear.Length];
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Encrypt(nonce, clear, cipher, tag, Encoding.UTF8.GetBytes("YazmaBackup.SettingsTransfer.v1"));
                var envelope = new SettingsEnvelope(
                    1,
                    "PBKDF2-SHA256/AES-256-GCM",
                    Iterations,
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(nonce),
                    Convert.ToBase64String(tag),
                    Convert.ToBase64String(cipher));
                var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
                return new SettingsExportResponse(
                    $"YazmaBackup_Settings_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.ybsettings",
                    Convert.ToBase64String(envelopeBytes),
                    payload.CreatedAtUtc);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task<SettingsImportResponse> ImportAsync(string passphrase, string bundleBase64, CancellationToken ct)
    {
        ValidatePassphrase(passphrase);
        if (string.IsNullOrWhiteSpace(bundleBase64)) throw new ArgumentException("Ayar paketi boş.");

        byte[] envelopeBytes;
        try { envelopeBytes = Convert.FromBase64String(bundleBase64); }
        catch (FormatException ex) { throw new ArgumentException("Ayar paketi Base64 biçiminde değil.", ex); }
        if (envelopeBytes.Length > MaxBundleBytes) throw new ArgumentException("Ayar paketi izin verilen boyutu aşıyor.");

        SettingsEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SettingsEnvelope>(envelopeBytes)
                ?? throw new InvalidDataException("Ayar paketi okunamadı.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Ayar paketi JSON biçimi geçersiz.", ex); }

        if (envelope.SchemaVersion != 1 || envelope.Iterations != Iterations ||
            !string.Equals(envelope.Algorithm, "PBKDF2-SHA256/AES-256-GCM", StringComparison.Ordinal))
            throw new InvalidDataException("Desteklenmeyen ayar paketi sürümü veya şifreleme algoritması.");

        var salt = Convert.FromBase64String(envelope.SaltBase64);
        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var cipher = Convert.FromBase64String(envelope.CiphertextBase64);
        if (salt.Length != SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes || cipher.Length > MaxBundleBytes)
            throw new InvalidDataException("Ayar paketi kriptografik alanları geçersiz.");

        var clear = new byte[cipher.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            try
            {
                aes.Decrypt(nonce, cipher, tag, clear, Encoding.UTF8.GetBytes("YazmaBackup.SettingsTransfer.v1"));
            }
            catch (CryptographicException ex)
            {
                throw new UnauthorizedAccessException("Ayar paketi parolası yanlış veya paket değiştirilmiş.", ex);
            }

            var payload = JsonSerializer.Deserialize<SettingsPayload>(clear)
                ?? throw new InvalidDataException("Ayar paketi içeriği okunamadı.");
            if (payload.SchemaVersion != 1) throw new InvalidDataException("Desteklenmeyen ayar içeriği sürümü.");

            return await ApplyAsync(payload, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private async Task<SettingsImportResponse> ApplyAsync(SettingsPayload payload, CancellationToken ct)
    {
        var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
        var agentByMachine = agents
            .GroupBy(x => x.MachineName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);

        var assignedApplied = 0;
        var missingMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in payload.Agents ?? [])
        {
            if (!agentByMachine.TryGetValue(item.MachineName, out var agent))
            {
                missingMachines.Add(item.MachineName);
                continue;
            }

            await store.SetAgentAssignedUserAsync(agent.AgentId, item.AssignedUser, ct).ConfigureAwait(false);
            assignedApplied++;
        }

        var existingPolicies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
        var created = 0;
        var skipped = 0;

        foreach (var item in payload.Policies ?? [])
        {
            if (!agentByMachine.TryGetValue(item.MachineName, out var agent))
            {
                missingMachines.Add(item.MachineName);
                continue;
            }

            var duplicate = existingPolicies.Any(p =>
                p.AgentId == agent.AgentId &&
                string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.RepositoryId, item.RepositoryId, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                skipped++;
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var interval = Math.Clamp(item.IntervalMinutes, 5, 43200);
            var policy = new BackupPolicyRecord(
                Guid.NewGuid(),
                item.Name,
                agent.AgentId,
                item.SourcePath,
                item.RepositoryRoot,
                item.RepositoryId,
                item.RequireSnapshot,
                interval,
                item.ActiveBytesPerSecond,
                item.IdleBytesPerSecond,
                item.UserIdleThresholdSeconds,
                item.Retention,
                item.Enabled,
                now,
                null,
                now.AddMinutes(interval),
                item.Protection,
                item.RestoreDrillIntervalDays,
                null,
                now.AddDays(item.RestoreDrillIntervalDays),
                item.RepositoryHealthIntervalHours,
                null,
                now.AddHours(item.RepositoryHealthIntervalHours));

            await store.CreateBackupPolicyAsync(policy, ct).ConfigureAwait(false);
            created++;
        }

        var nasQueued = 0;
        var nasSkipped = 0;
        if (payload.GlobalNas is not null)
        {
            var saved = await nasProfiles.SaveAsync(
                payload.GlobalNas.RepositoryId,
                payload.GlobalNas.RepositoryRoot,
                payload.GlobalNas.Username,
                payload.GlobalNas.Password,
                ct).ConfigureAwait(false);
            var apply = await globalNas.ApplyToAllAgentsAsync(saved, ct).ConfigureAwait(false);
            nasQueued = apply.Queued;
            nasSkipped = apply.Skipped;
        }

        return new SettingsImportResponse(
            assignedApplied,
            created,
            skipped,
            missingMachines.Count,
            nasQueued,
            nasSkipped);
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12 || passphrase.Length > 256)
            throw new ArgumentException("Dışa aktarma parolası 12-256 karakter olmalıdır.");
    }

    private sealed record SettingsEnvelope(
        int SchemaVersion,
        string Algorithm,
        int Iterations,
        string SaltBase64,
        string NonceBase64,
        string TagBase64,
        string CiphertextBase64);

    private sealed record SettingsPayload(
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        ExportNasProfile? GlobalNas,
        ExportAgentSettings[] Agents,
        ExportPolicy[] Policies);

    private sealed record ExportNasProfile(string RepositoryId, string RepositoryRoot, string Username, string Password);
    private sealed record ExportAgentSettings(string MachineName, string? AssignedUser);
    private sealed record ExportPolicy(
        string MachineName,
        string Name,
        string SourcePath,
        string RepositoryRoot,
        string RepositoryId,
        bool RequireSnapshot,
        int IntervalMinutes,
        long ActiveBytesPerSecond,
        long IdleBytesPerSecond,
        int UserIdleThresholdSeconds,
        RetentionPolicy Retention,
        ProtectionPolicy Protection,
        bool Enabled,
        int RestoreDrillIntervalDays,
        int RepositoryHealthIntervalHours);
}
