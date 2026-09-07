using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class WindowsVssSnapshotProvider : ISnapshotProvider
{
    public async Task<SnapshotHandle> CreateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VSS snapshots are supported only on Windows.");

        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourcePath);
        var volumeRoot = Path.GetPathRoot(source) ?? throw new InvalidOperationException("Source volume could not be resolved.");
        if (volumeRoot.Length < 3 || volumeRoot[1] != ':' || volumeRoot[2] != Path.DirectorySeparatorChar)
            throw new NotSupportedException("VSS source must be on a local Windows drive volume.");

        var createScript = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $r = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{ Volume='{{EscapePowerShellLiteral(volumeRoot)}}'; Context='ClientAccessible' }
            if ([int]$r.ReturnValue -ne 0) { throw ('VSS create failed with code ' + $r.ReturnValue) }
            $s = Get-CimInstance -ClassName Win32_ShadowCopy -Filter ("ID = '" + $r.ShadowID + "'")
            if ($null -eq $s) { throw 'Created VSS snapshot could not be queried.' }
            [pscustomobject]@{ Id=[string]$s.ID; DeviceObject=[string]$s.DeviceObject } | ConvertTo-Json -Compress
            """;

        var result = await RunPowerShellAsync(createScript).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("VSS snapshot creation failed: " + SanitizeError(result.StandardError));

        var jsonLine = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? throw new InvalidDataException("VSS snapshot command returned no metadata.");
        var metadata = JsonSerializer.Deserialize<VssMetadata>(jsonLine)
            ?? throw new InvalidDataException("VSS snapshot metadata is invalid.");
        if (string.IsNullOrWhiteSpace(metadata.Id) || string.IsNullOrWhiteSpace(metadata.DeviceObject))
            throw new InvalidDataException("VSS snapshot metadata is incomplete.");

        var relative = Path.GetRelativePath(volumeRoot, source);
        var readable = relative == "."
            ? metadata.DeviceObject.TrimEnd('\\') + "\\"
            : metadata.DeviceObject.TrimEnd('\\') + "\\" + relative;

        return new SnapshotHandle(source, readable, snapshotBacked: true, async () =>
        {
            var deleteScript = $$"""
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                $s = Get-CimInstance -ClassName Win32_ShadowCopy -Filter "ID = '{{EscapePowerShellLiteral(metadata.Id)}}'"
                if ($null -ne $s) { $s | Remove-CimInstance }
                """;
            var cleanup = await RunPowerShellAsync(deleteScript).ConfigureAwait(false);
            if (cleanup.ExitCode != 0)
                throw new InvalidOperationException("VSS snapshot cleanup failed: " + SanitizeError(cleanup.StandardError));
        });
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(encoded);

        if (!process.Start()) throw new InvalidOperationException("powershell.exe could not be started for VSS operation.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SanitizeError(string value)
    {
        var compact = string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 1000 ? compact : compact[..1000];
    }

    private sealed record VssMetadata(string Id, string DeviceObject);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
