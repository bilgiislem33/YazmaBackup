using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using Microsoft.AspNetCore.Routing;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class MeshCentralPublicEndpoints
{
    internal static IEndpointRouteBuilder MapMeshCentralPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/bootstrap/meshcentral/{token}", async (string token, MeshCentralBootstrapTicketService tickets, IControlPlaneStore control, IMeshCentralFleetStore fleet, CancellationToken ct) =>
        {
            if (!tickets.TryConsumeBootstrap(token, out var bootstrap) || bootstrap is null) return Results.NotFound();
            var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
            var publicBase = Environment.GetEnvironmentVariable("YAZMABACKUP_PUBLIC_BASE_URI");
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath) || !Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps)
                return Results.Problem("Agent deployment package is not configured.", statusCode: 503);
            var enrollment = await control.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(15), 1, ct).ConfigureAwait(false);
            tickets.TrackEnrollment(enrollment.EnrollmentToken, bootstrap.NodeId, bootstrap.DeploymentId, TimeSpan.FromMinutes(15));
            var package = tickets.IssuePackage(bootstrap.DeploymentId, TimeSpan.FromMinutes(10));
            var packageUrl = new Uri(publicUri, "/api/v1/bootstrap/package/" + Uri.EscapeDataString(package.Token)).ToString();
            var server = publicUri.GetLeftPart(UriPartial.Authority);
            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath, ct).ConfigureAwait(false))).ToLowerInvariant();
            var deploymentTag = bootstrap.DeploymentId.ToString("N");
            var script = "$ErrorActionPreference='Stop';$w=Join-Path $env:ProgramData ('YazmaBackupDeploy\\'+[guid]::NewGuid().ToString('N'));$fallbackLogRoot=Join-Path $env:ProgramData 'YazmaBackupDeployLogs';New-Item -ItemType Directory -Force -Path $w,$fallbackLogRoot|Out-Null;$tempLog=Join-Path $w 'meshcentral-install-" + deploymentTag + ".log';$fallbackLog=Join-Path $fallbackLogRoot 'meshcentral-install-" + deploymentTag + ".log';$canonicalLog=Join-Path $env:ProgramData 'YazmaBackup\\Logs\\meshcentral-install-" + deploymentTag + ".log';$z=Join-Path $w 'agent.zip';$installSucceeded=$false;try{Invoke-WebRequest -UseBasicParsing -Uri '" + packageUrl.Replace("'", "''", StringComparison.Ordinal) + "' -OutFile $z;if((Get-FileHash -Algorithm SHA256 $z).Hash.ToLowerInvariant() -ne '" + sha + "'){throw 'YazmaBackup Agent package SHA-256 mismatch.'};Expand-Archive $z (Join-Path $w 'bundle') -Force;$env:YAZMABACKUP_ENROLLMENT_TOKEN='" + enrollment.EnrollmentToken + "';$installer=Join-Path $w 'bundle\\INSTALL_AGENT.ps1';$agentPath=Join-Path $w 'bundle\\agent';& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -PublishedAgentPath $agentPath -Server '" + server.Replace("'", "''", StringComparison.Ordinal) + "' *>&1 | Tee-Object -FilePath $tempLog;$installExit=$LASTEXITCODE;if($installExit -ne 0){Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue;$tail=((Get-Content -LiteralPath $tempLog -Tail 12 -ErrorAction SilentlyContinue)-join ' | ');throw ('INSTALL_AGENT.ps1 failed. ExitCode='+$installExit+'; Detail='+$tail+'; FallbackLog='+$fallbackLog)};$svc=Get-Service -Name 'YazmaBackupAgent' -ErrorAction SilentlyContinue;if($null -eq $svc -or $svc.Status -ne 'Running'){throw ('YazmaBackupAgent service is not Running after installer. TempLog='+$tempLog)};$identity=Join-Path $env:ProgramData 'YazmaBackup\\agent.json';$access=Join-Path $env:ProgramData 'YazmaBackup\\agent-access-token.dpapi';$deadline=[DateTimeOffset]::UtcNow.AddSeconds(90);while([DateTimeOffset]::UtcNow -lt $deadline -and (-not(Test-Path -LiteralPath $identity -PathType Leaf) -or -not(Test-Path -LiteralPath $access -PathType Leaf))){Start-Sleep -Seconds 2;$svc=Get-Service -Name 'YazmaBackupAgent' -ErrorAction SilentlyContinue;if($null -eq $svc -or $svc.Status -ne 'Running'){throw ('YazmaBackupAgent stopped before enrollment evidence was created. TempLog='+$tempLog)}};if(-not(Test-Path -LiteralPath $identity -PathType Leaf) -or -not(Test-Path -LiteralPath $access -PathType Leaf)){throw ('Agent enrollment evidence was not created within 90 seconds. TempLog='+$tempLog)};Add-Content -LiteralPath $tempLog -Value 'PASS: service=Running; enrollment-evidence=present';$installSucceeded=$true;try{$canonicalLogRoot=Split-Path -Parent $canonicalLog;New-Item -ItemType Directory -Force -Path $canonicalLogRoot|Out-Null;Copy-Item -LiteralPath $tempLog -Destination $canonicalLog -Force -ErrorAction Stop}catch{Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue}}catch{if(Test-Path -LiteralPath $tempLog -PathType Leaf){Copy-Item -LiteralPath $tempLog -Destination $fallbackLog -Force -ErrorAction SilentlyContinue};throw}finally{Remove-Item Env:YAZMABACKUP_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue;if($installSucceeded -and (Test-Path -LiteralPath $fallbackLog -PathType Leaf)){Remove-Item -LiteralPath $fallbackLog -Force -ErrorAction SilentlyContinue};Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue}";
            await fleet.UpdateMeshCentralDeploymentAsync(bootstrap.DeploymentId, "installing", "Bootstrap ticket consumed by target device.", null, null, ct).ConfigureAwait(false);
            return Results.Ok(new { script });
        }).RequireRateLimiting("integration");

        endpoints.MapGet("/api/v1/bootstrap/package/{token}", (string token, MeshCentralBootstrapTicketService tickets) =>
        {
            if (!tickets.TryConsumePackage(token, out var package) || package is null) return Results.NotFound();
            var packagePath = Environment.GetEnvironmentVariable("YAZMABACKUP_AGENT_PACKAGE_ZIP");
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return Results.NotFound();
            return Results.File(packagePath, "application/zip", "YazmaBackupAgent.zip", enableRangeProcessing: false);
        }).RequireRateLimiting("integration");

        endpoints.MapPost("/api/v1/integrations/meshcentral/status", async (HttpContext http, MeshCentralStatusReportRequest request, IProductionFabricStore store, IControlPlaneStore auditStore, CancellationToken ct) =>
        {
            var authorization = http.Request.Headers.Authorization.FirstOrDefault();
            const string prefix = "Bearer ";
            if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
            var token = authorization[prefix.Length..].Trim();
            var credential = await store.AuthenticateIntegrationCredentialAsync(token, "meshcentral-status", ct).ConfigureAwait(false);
            if (credential is null) return Results.Unauthorized();
            try
            {
                var received = DateTimeOffset.UtcNow;
                var record = await store.ReportMeshCentralSyncAsync(credential.CredentialId, request.EventId, request.AgentId, request.NodeId, request.NodeStatus, request.DeploymentStatus, request.ReportedAtUtc, received, ct).ConfigureAwait(false);
                await auditStore.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), received, $"integration:{credential.Name}", "meshcentral-status", "POST", "/api/v1/integrations/meshcentral/status", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
                return Results.Ok(new MeshCentralSyncEventDto(record.EventId, record.AgentId, record.NodeId, record.NodeStatus, record.DeploymentStatus, record.ReportedAtUtc, record.ReceivedAtUtc, record.CredentialId));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireRateLimiting("integration");

        return endpoints;
    }
}
