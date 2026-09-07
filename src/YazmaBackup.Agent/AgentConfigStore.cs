using System.Text.Json;

namespace YazmaBackup.Agent;

public sealed record AgentConfig(
    string Server,
    int PollSeconds,
    int LeaseRenewSeconds,
    bool AllowLiveReadFallback,
    bool AllowInsecureHttp,
    string UpdatePublicKeyPath);

public static class AgentConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AgentConfig Read()
    {
        AgentPaths.EnsureDirectories();
        if (!File.Exists(AgentPaths.ConfigFile))
            throw new FileNotFoundException("Agent configuration is missing. Run --bootstrap before starting the service.", AgentPaths.ConfigFile);

        var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.ConfigFile), JsonOptions)
            ?? throw new InvalidDataException("Agent configuration could not be deserialized.");
        var serverOverride = Environment.GetEnvironmentVariable("YAZMABACKUP_SERVER");
        if (!string.IsNullOrWhiteSpace(serverOverride)) config = config with { Server = serverOverride.Trim() };
        Validate(config);
        return config with { Server = config.Server.TrimEnd('/') };
    }

    public static void Write(AgentConfig config)
    {
        Validate(config);
        AgentPaths.EnsureDirectories();
        var temp = AgentPaths.ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config with { Server = config.Server.TrimEnd('/') }, JsonOptions));
            File.Move(temp, AgentPaths.ConfigFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void Validate(AgentConfig config)
    {
        if (!Uri.TryCreate(config.Server, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidDataException("Agent server must be an absolute http:// or https:// URL.");
        if (uri.Scheme == Uri.UriSchemeHttp && !config.AllowInsecureHttp && !uri.IsLoopback)
            throw new InvalidDataException("Plain HTTP is disabled. Use HTTPS or explicitly bootstrap with insecure HTTP enabled for a lab environment.");
        if (config.PollSeconds is < 5 or > 300)
            throw new InvalidDataException("PollSeconds must be between 5 and 300.");
        if (config.LeaseRenewSeconds is < 10 or > 60)
            throw new InvalidDataException("LeaseRenewSeconds must be between 10 and 60.");
        if (string.IsNullOrWhiteSpace(config.UpdatePublicKeyPath))
            throw new InvalidDataException("UpdatePublicKeyPath is required.");
    }
}
