using System.Text.Json;

namespace YazmaBackup.Agent;

public static class AgentLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Info(string eventName, string message, object? data = null) => Write("information", eventName, message, data);
    public static void Warning(string eventName, string message, object? data = null) => Write("warning", eventName, message, data);
    public static void Error(string eventName, string message, object? data = null) => Write("error", eventName, message, data);

    private static void Write(string level, string eventName, string message, object? data)
    {
        try
        {
            AgentPaths.EnsureDirectories();
            var entry = JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                level,
                eventName,
                message,
                data
            }, JsonOptions);
            var path = Path.Combine(AgentPaths.LogDirectory, $"agent-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
            lock (Gate)
            {
                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never terminate the backup agent.
        }
    }
}
