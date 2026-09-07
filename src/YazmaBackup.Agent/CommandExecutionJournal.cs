using System.Text.Json;

namespace YazmaBackup.Agent;

public sealed record CachedCommandResult(Guid CommandId, bool Succeeded, string ResultJson, string? Error, DateTimeOffset CompletedAtUtc);

public static class CommandExecutionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static CachedCommandResult? Read(Guid commandId)
    {
        AgentPaths.EnsureDirectories();
        var path = GetPath(commandId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CachedCommandResult>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Cached command result is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Cached command result is corrupt: {commandId}", ex);
        }
    }

    public static void Write(CachedCommandResult result)
    {
        AgentPaths.EnsureDirectories();
        var path = GetPath(result.CommandId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Cleanup(TimeSpan retention)
    {
        AgentPaths.EnsureDirectories();
        var threshold = DateTimeOffset.UtcNow.Subtract(retention);
        foreach (var file in Directory.EnumerateFiles(AgentPaths.CommandResultDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < threshold.UtcDateTime) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetPath(Guid commandId) => Path.Combine(AgentPaths.CommandResultDirectory, commandId.ToString("N") + ".json");
}
