using System.Text.Json;

namespace YazmaBackup.Agent;

public sealed record AgentIdentity(Guid AgentId);

public static class AgentIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static AgentIdentity? Read()
    {
        AgentPaths.EnsureDirectories();
        if (!File.Exists(AgentPaths.IdentityFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(AgentPaths.IdentityFile), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Agent identity file is corrupt.", ex);
        }
    }

    public static void Write(AgentIdentity identity)
    {
        AgentPaths.EnsureDirectories();
        var temp = AgentPaths.IdentityFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(identity, JsonOptions));
            File.Move(temp, AgentPaths.IdentityFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
