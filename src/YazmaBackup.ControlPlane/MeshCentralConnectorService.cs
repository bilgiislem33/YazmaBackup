using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed class MeshCentralConnectorService
{
    private static readonly Action<ILogger, string, Exception?> LogConnectionTestFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1201, nameof(LogConnectionTestFailed)),
        "MeshCentral connection test failed for {BaseUri}");

    private readonly IMeshCentralFleetStore _fleet;
    private readonly IControlPlaneStore _control;
    private readonly IDataProtector _protector;
    private readonly ILogger<MeshCentralConnectorService> _logger;

    public MeshCentralConnectorService(IMeshCentralFleetStore fleet, IControlPlaneStore control, IDataProtectionProvider dataProtectionProvider, ILogger<MeshCentralConnectorService> logger)
    {
        _fleet = fleet;
        _control = control;
        _protector = dataProtectionProvider.CreateProtector("YazmaBackup.MeshCentralConnector.Credential.v1");
        _logger = logger;
    }

    public string ProtectCredential(string plaintext) => _protector.Protect(plaintext);

    public async Task<(bool Succeeded, string Message, string? ServerVersion, string? MeshCtrlPath)> TestAsync(MeshCentralConnectorRecord connector, CancellationToken ct)
    {
        try
        {
            var path = ResolveMeshCtrlPath(connector.MeshCtrlPath);
            var output = await RunMeshCtrlTextAsync(connector, path, "ServerInfo", [], ct).ConfigureAwait(false);
            var info = ParseServerInfo(output);
            if (!info.ContainsKey("name") && !info.ContainsKey("domain") && !info.ContainsKey("https"))
                throw new InvalidDataException("meshctrl ServerInfo geçerli sunucu bilgisi döndürmedi.");
            var version = GetMapValue(info, "version") ?? GetMapValue(info, "meshversion");
            var serverName = GetMapValue(info, "name");
            return (true, serverName is null ? "MeshCentral bağlantısı başarılı." : $"MeshCentral bağlantısı başarılı: {serverName}", version, path);
        }
        catch (Exception ex)
        {
            LogConnectionTestFailed(_logger, connector.BaseUri, ex);
            return (false, SanitizeError(ex.Message), null, null);
        }
    }

    public async Task<IReadOnlyList<MeshCentralInventoryDeviceRecord>> SynchronizeAsync(MeshCentralConnectorRecord connector, CancellationToken ct)
    {
        var path = ResolveMeshCtrlPath(connector.MeshCtrlPath);
        var serverInfoText = await RunMeshCtrlTextAsync(connector, path, "ServerInfo", [], ct).ConfigureAwait(false);
        using var deviceJson = await RunMeshCtrlJsonAsync(connector, path, "ListDevices", ["--json"], ct).ConfigureAwait(false);
        var agents = await _control.GetAgentsAsync(ct).ConfigureAwait(false);
        var observed = DateTimeOffset.UtcNow;
        var devices = ParseDevices(deviceJson.RootElement, connector.ConnectorId, observed, agents);
        var serverInfo = ParseServerInfo(serverInfoText);
        var version = GetMapValue(serverInfo, "version") ?? GetMapValue(serverInfo, "meshversion");
        await _fleet.SaveMeshCentralInventoryAsync(connector.ConnectorId, devices, observed, version, ct).ConfigureAwait(false);
        var deployments = await _fleet.GetMeshCentralDeploymentsAsync(5000, ct).ConfigureAwait(false);
        foreach (var deployment in deployments.Where(x => x.Status is "queued" or "dispatching" or "dispatched" or "installing"))
        {
            var device = devices.FirstOrDefault(x => x.NodeId == deployment.NodeId);
            if (device is not null && device.MatchStatus == "matched" && device.MatchedAgentId is not null)
            {
                await _fleet.UpdateMeshCentralDeploymentAsync(deployment.DeploymentId, "succeeded", $"Agent heartbeat matched after MeshCentral deployment. Version={device.AgentVersion ?? "unknown"}", deployment.CommandId, device.MatchedAgentId, ct).ConfigureAwait(false);
                continue;
            }

            var elapsed = observed - deployment.RequestedAtUtc;
            if (device is not null && device.Online && elapsed > TimeSpan.FromMinutes(30))
            {
                await _fleet.UpdateMeshCentralDeploymentAsync(deployment.DeploymentId, "failed", "MeshCentral cihazı çevrimiçi kaldı ancak 30 dakikalık deployment penceresinde exact YazmaBackup Agent kaydı/heartbeat kanıtı oluşmadı.", deployment.CommandId, deployment.AgentId, ct).ConfigureAwait(false);
                continue;
            }

            // R5.9: for "installing" deployments, local install evidence (service Running + agent.json/token)
            // has already succeeded, so a hostname mismatch/ambiguity against MeshCentral's inventory - not an
            // offline endpoint - is the dominant real-world cause of an indefinite stall. Surface the concrete
            // match diagnostic in the deployment detail instead of a static "waiting" message that never changes,
            // and stop waiting forever even when MeshCentral never reports the device Online (missing/false online
            // flags would otherwise bypass the 30-minute failure check above entirely).
            if (deployment.Status == "installing" && device is not null && device.MatchStatus is "missing" or "ambiguous")
            {
                if (elapsed > TimeSpan.FromMinutes(30))
                {
                    await _fleet.UpdateMeshCentralDeploymentAsync(deployment.DeploymentId, "failed", $"Agent kaydı görüldü ancak 30 dakikalık deployment penceresinde eşleşen heartbeat oluşmadı. MeshCentral eşleşme durumu={device.MatchStatus}; kanıt={device.MatchEvidence ?? "yok"}.", deployment.CommandId, deployment.AgentId, ct).ConfigureAwait(false);
                }
                else
                {
                    await _fleet.UpdateMeshCentralDeploymentAsync(deployment.DeploymentId, deployment.Status, $"Remote installer, service and local enrollment evidence completed; fleet heartbeat match not yet found. MeshCentral eşleşme durumu={device.MatchStatus}; kanıt={device.MatchEvidence ?? "yok"}.", deployment.CommandId, deployment.AgentId, ct).ConfigureAwait(false);
                }
            }
        }
        return devices;
    }

    public async Task<(string CommandId, string Output)> RunPowerShellAsync(MeshCentralConnectorRecord connector, string nodeId, string script, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Any(char.IsControl)) throw new ArgumentException("MeshCentral node id is invalid.", nameof(nodeId));
        var path = ResolveMeshCtrlPath(connector.MeshCtrlPath);
        var output = await RunMeshCtrlTextAsync(connector, path, "RunCommand", ["--id", nodeId, "--powershell", "--reply", "--run", script], ct).ConfigureAwait(false);
        var commandId = Guid.NewGuid().ToString("N");
        try
        {
            using var json = JsonDocument.Parse(ExtractJson(output));
            commandId = TryFindString(json.RootElement, "commandId") ?? TryFindString(json.RootElement, "id") ?? commandId;
        }
        catch (JsonException) { }
        catch (InvalidDataException) { }
        return (commandId, output.Trim());
    }

    private async Task<JsonDocument> RunMeshCtrlJsonAsync(MeshCentralConnectorRecord connector, string meshCtrlPath, string command, IReadOnlyList<string> extra, CancellationToken ct)
    {
        var stdout = await RunMeshCtrlTextAsync(connector, meshCtrlPath, command, extra, ct).ConfigureAwait(false);
        return JsonDocument.Parse(ExtractJson(stdout));
    }

    private async Task<string> RunMeshCtrlTextAsync(MeshCentralConnectorRecord connector, string meshCtrlPath, string command, IReadOnlyList<string> extra, CancellationToken ct)
    {
        var node = ResolveNodeExecutable();
        var bridgePath = ResolveMeshCtrlBridgePath();
        var args = new List<string>
        {
            Quote(bridgePath),
            Quote(meshCtrlPath),
            command,
            "--url",
            Quote(ToWebSocketBase(connector.BaseUri)),
            "--loginuser",
            Quote(connector.Username)
        };
        args.AddRange(extra.Select(Quote));

        var credential = _protector.Unprotect(connector.ProtectedCredential);
        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = string.Join(' ', args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["YAZMABACKUP_MESHCTRL_AUTH_MODE"] = connector.AuthenticationMode;
        psi.Environment["YAZMABACKUP_MESHCTRL_CREDENTIAL"] = credential;

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) throw new InvalidOperationException("meshctrl process could not be started.");
            credential = string.Empty;
            psi.Environment.Remove("YAZMABACKUP_MESHCTRL_CREDENTIAL");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"meshctrl failed (exit {process.ExitCode}): {SanitizeError(stderr)}");
            if (string.IsNullOrWhiteSpace(stdout)) throw new InvalidDataException("meshctrl boş çıktı döndürdü.");
            var combined = stdout + "\n" + stderr;
            if (ContainsMeshCtrlFailure(combined))
                throw new InvalidOperationException("meshctrl command failed: " + SanitizeError(combined));
            return stdout;
        }
        finally
        {
            credential = string.Empty;
            psi.Environment.Remove("YAZMABACKUP_MESHCTRL_CREDENTIAL");
        }
    }

    private static bool ContainsMeshCtrlFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var patterns = new[]
        {
            "Missing run, use --run",
            "Unknown command",
            "Unknown argument",
            "Invalid arguments",
            "Unable to connect",
            "Authentication failed",
            "Login failed",
            "YAZMABACKUP_DEPLOYMENT_ERROR:"
        };
        return patterns.Any(pattern => output.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static MeshCentralInventoryDeviceRecord[] ParseDevices(JsonElement root, Guid connectorId, DateTimeOffset observed, IReadOnlyList<AgentRecord> agents)
    {
        var list = new List<MeshCentralInventoryDeviceRecord>();
        foreach (var item in EnumerateDeviceObjects(root))
        {
            var nodeId = GetString(item, "_id") ?? GetString(item, "nodeid") ?? GetString(item, "nodeId") ?? GetString(item, "id");
            if (string.IsNullOrWhiteSpace(nodeId)) continue;
            var name = GetString(item, "name") ?? GetString(item, "hostname") ?? GetString(item, "rname") ?? nodeId;
            var remoteName = GetString(item, "rname");
            var hostname = GetString(item, "hostname") ?? remoteName ?? name;
            var domain = GetString(item, "domain");
            var group = GetString(item, "meshname") ?? GetString(item, "groupname") ?? GetString(item, "group");
            var online = GetBool(item, "online") ?? ((GetInt(item, "conn") ?? 0) != 0) || string.Equals(GetString(item, "status"), "online", StringComparison.OrdinalIgnoreCase);

            var primaryMatches = agents.Where(a => HostEquals(a.MachineName, hostname)).ToArray();
            var fallbackMatches = primaryMatches.Length == 0 && !HostEquals(name, hostname)
                ? agents.Where(a => HostEquals(a.MachineName, name)).ToArray()
                : [];
            var matches = primaryMatches.Length > 0 ? primaryMatches : fallbackMatches;
            var source = primaryMatches.Length > 0 ? (remoteName is null ? "hostname" : "rname") : "name";
            var status = matches.Length switch { 0 => "missing", 1 => "matched", _ => "ambiguous" };
            var evidence = matches.Length switch
            {
                0 => $"hostname eşleşmesi bulunamadı; öncelik={hostname}",
                1 => $"{source}:{hostname} → {matches[0].MachineName}",
                _ => $"{source}:{hostname} için {matches.Length} olası Agent eşleşmesi"
            };
            var matched = matches.Length == 1 ? matches[0] : null;
            list.Add(new MeshCentralInventoryDeviceRecord(nodeId, connectorId, name, hostname, domain, group, online, observed, matched?.AgentId, status, evidence, matched?.AgentVersion));
        }
        return list.GroupBy(x => x.NodeId, StringComparer.Ordinal).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<JsonElement> EnumerateDeviceObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) { foreach (var x in root.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) yield return x; yield break; }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array) foreach (var x in p.Value.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) yield return x;
            else if (p.Value.ValueKind == JsonValueKind.Object && (p.Value.TryGetProperty("_id", out _) || p.Value.TryGetProperty("nodeid", out _))) yield return p.Value;
        }
    }

    private static bool HostEquals(string? left, string? right)
    {
        static string N(string? value) => (value ?? string.Empty).Trim().Split('.')[0];
        return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(N(left), N(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNodeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return OperatingSystem.IsWindows() ? "node.exe" : "node";
    }

    public static string ResolveMeshCtrlPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("YAZMABACKUP_MESHCTRL_PATH"),
            CombineEnvironmentPath("NVM_SYMLINK", "node_modules", "meshcentral", "meshctrl.js"),
            CombineEnvironmentPath("NVM_HOME", "node_modules", "meshcentral", "meshctrl.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "meshcentral", "meshctrl.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules", "meshcentral", "meshctrl.js"),
            Path.Combine(AppContext.BaseDirectory, "tools", "meshctrl.js")
        };
        var found = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (found is null) throw new FileNotFoundException("meshctrl.js bulunamadı. MeshCentral/meshctrl kurulumunu yapın veya YAZMABACKUP_MESHCTRL_PATH tanımlayın.");
        return Path.GetFullPath(found);
    }

    private static string? CombineEnvironmentPath(string variable, params string[] segments)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(root)) return null;
        var parts = new string[segments.Length + 1];
        parts[0] = root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Combine(parts);
    }

    private static string ToWebSocketBase(string baseUri)
    {
        var uri = new Uri(baseUri);
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.ToString().TrimEnd('/');
    }

    private static string ResolveMeshCtrlBridgePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "meshctrl-bridge.js"),
            Path.Combine(AppContext.BaseDirectory, "meshctrl-bridge.js"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "meshctrl-bridge.js"))
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) throw new FileNotFoundException("meshctrl-bridge.js bulunamadı. Release paketi eksik veya bozuk.");
        return found;
    }

    private static Dictionary<string, string> ParseServerInfo(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0 && !map.ContainsKey(key)) map[key] = value;
        }
        return map;
    }

    private static string? GetMapValue(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        var objectPos = trimmed.IndexOf('{');
        var arrayPos = trimmed.IndexOf('[');
        var start = objectPos < 0 ? arrayPos : arrayPos < 0 ? objectPos : Math.Min(objectPos, arrayPos);
        if (start < 0) throw new InvalidDataException("meshctrl JSON output was not found.");
        return trimmed[start..];
    }

    private static string? TryFindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return p.Value.ToString();
                var nested = TryFindString(p.Value, name); if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var x in element.EnumerateArray()) { var nested = TryFindString(x, name); if (nested is not null) return nested; }
        return null;
    }

    private static string? GetString(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
    private static int? GetInt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : null;
    private static bool? GetBool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string SanitizeError(string value)
    {
        var sanitized = string.Join(' ', value.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)).Trim().Replace("password", "credential", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length <= 600 ? sanitized : sanitized[..600];
    }
}
