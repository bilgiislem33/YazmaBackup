using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using YazmaBackup.Contracts;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class AgentUpdateApplier(AgentConfig config)
{
    public ApplyStagedAgentUpdateResultDto Schedule(ApplyStagedAgentUpdatePayload payload)
    {
        if (!Version.TryParse(payload.Version, out var requested))
            throw new InvalidDataException("Update version is invalid.");
        var current = Version.Parse(AgentWorker.AgentVersion);
        if (requested <= current)
            throw new InvalidOperationException($"Update version {requested} must be newer than installed version {current}.");

        var versionDirectory = Path.Combine(AgentPaths.UpdateDirectory, payload.Version);
        var readyPath = Path.Combine(versionDirectory, "update-ready.json");
        var packagePath = Path.Combine(versionDirectory, "agent-package.zip");
        if (!File.Exists(readyPath) || !File.Exists(packagePath))
            throw new FileNotFoundException("Staged update package is not ready.");

        using (var readyDoc = JsonDocument.Parse(File.ReadAllText(readyPath)))
        {
            var stagedVersion = readyDoc.RootElement.GetProperty("version").GetString();
            var verified = readyDoc.RootElement.GetProperty("signatureVerified").GetBoolean();
            if (!verified || !string.Equals(stagedVersion, payload.Version, StringComparison.Ordinal))
                throw new InvalidDataException("Staged update verification evidence is invalid.");
        }

        var applyRoot = Path.Combine(versionDirectory, "apply");
        if (Directory.Exists(applyRoot)) Directory.Delete(applyRoot, recursive: true);
        Directory.CreateDirectory(applyRoot);
        ZipFile.ExtractToDirectory(packagePath, applyRoot, overwriteFiles: true);

        var installScript = Path.Combine(applyRoot, "INSTALL_AGENT.ps1");
        var agentDirectory = Path.Combine(applyRoot, "agent");
        if (!File.Exists(installScript) || !File.Exists(Path.Combine(agentDirectory, "YazmaBackup.Agent.exe")))
            throw new InvalidDataException("Update package must contain INSTALL_AGENT.ps1 and agent/YazmaBackup.Agent.exe.");

        var launcher = Path.Combine(versionDirectory, "apply-update.ps1");
        var script = $$"""
$ErrorActionPreference='Stop'
Start-Sleep -Seconds 8
$install={{PsQuote(installScript)}}
$agent={{PsQuote(agentDirectory)}}
$server={{PsQuote(config.Server)}}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install -PublishedAgentPath $agent -Server $server
if($LASTEXITCODE -ne 0){ exit $LASTEXITCODE }
""";
        File.WriteAllText(launcher, script);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(launcher);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Update launcher could not be started.");

        // The installer preserves ProgramData identity/token/config/key stores and existing
        // Control Plane policies remain server-side. The current Agent returns the command
        // result before the detached launcher replaces the Windows service image.
        return new ApplyStagedAgentUpdateResultDto(payload.Version, launcher, true, true, true);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
