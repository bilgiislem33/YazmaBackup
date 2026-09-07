using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace YazmaBackup.ControlPlane;

public sealed record GlobalNasProfile(
    string RepositoryId,
    string RepositoryRoot,
    string Username,
    string Password,
    long Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record GlobalNasProfileSummary(
    string RepositoryId,
    string RepositoryRoot,
    string Username,
    long Version,
    DateTimeOffset UpdatedAtUtc,
    bool PasswordConfigured);

public sealed class GlobalNasProfileStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GlobalNasProfileStore(string stateRoot, IDataProtectionProvider provider)
    {
        _path = Path.Combine(stateRoot, "global-nas-profile.protected");
        _protector = provider.CreateProtector("YazmaBackup.GlobalNasProfile.v1");
    }

    public async Task<GlobalNasProfile?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            var protectedText = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            var json = _protector.Unprotect(protectedText);
            return JsonSerializer.Deserialize<GlobalNasProfile>(json)
                ?? throw new InvalidDataException("Global NAS profile could not be deserialized.");
        }
        finally { _gate.Release(); }
    }

    public async Task<GlobalNasProfile> SaveAsync(string repositoryId, string repositoryRoot, string username, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long nextVersion = 1;
            if (File.Exists(_path))
            {
                try
                {
                    var currentJson = _protector.Unprotect(await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false));
                    var current = JsonSerializer.Deserialize<GlobalNasProfile>(currentJson);
                    nextVersion = (current?.Version ?? 0) + 1;
                }
                catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
                {
                    nextVersion = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }

            var profile = new GlobalNasProfile(
                repositoryId.Trim(),
                repositoryRoot.Trim(),
                username.Trim(),
                password,
                nextVersion,
                DateTimeOffset.UtcNow);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            var protectedText = _protector.Protect(JsonSerializer.Serialize(profile));
            await File.WriteAllTextAsync(temp, protectedText, ct).ConfigureAwait(false);
            File.Move(temp, _path, true);
            return profile;
        }
        finally { _gate.Release(); }
    }

    public static GlobalNasProfileSummary ToSummary(GlobalNasProfile profile) =>
        new(profile.RepositoryId, profile.RepositoryRoot, profile.Username, profile.Version, profile.UpdatedAtUtc, !string.IsNullOrEmpty(profile.Password));
}
