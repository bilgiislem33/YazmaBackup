using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class FleetMonitoringEndpoints
{
    internal static AdminEndpointGroups MapFleetMonitoringEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/agents", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            return Results.Ok(agents.Select(a => new AgentSummaryDto(
                a.AgentId, a.MachineName, a.OperatingSystem, a.AgentVersion,
                a.Capabilities ?? [], a.EnrolledAtUtc, a.LastSeenUtc, a.ProtectionStatus, a.ProtectionReason, a.ProtectionTriggeredAtUtc, a.ProtectionIncidentId, a.RepositoryCircuits ?? [], a.AssignedUser)));
        });

        groups.Read.MapGet("/fleet/lifecycle", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
            var rows = agents.Select(a =>
            {
                var ownedPolicies = policies.Where(p => p.AgentId == a.AgentId).ToArray();
                return new
                {
                    a.AgentId, a.MachineName, a.AgentVersion, a.LastSeenUtc, a.AssignedUser,
                    policyCount = ownedPolicies.Length,
                    enabledPolicyCount = ownedPolicies.Count(p => p.Enabled),
                    identityPersistent = true,
                    policiesServerSide = true,
                    lifecycleState = DateTimeOffset.UtcNow - a.LastSeenUtc <= TimeSpan.FromMinutes(3) ? "managed" : "offline"
                };
            }).ToArray();
            return Results.Ok(new
            {
                total = rows.Length,
                managed = rows.Count(x => x.lifecycleState == "managed"),
                offline = rows.Count(x => x.lifecycleState == "offline"),
                policyCount = policies.Count,
                rows
            });
        });

        groups.Read.MapGet("/commands/{commandId:guid}", async (Guid commandId, IControlPlaneStore store, CancellationToken ct) =>
        {
            var cmd = await store.GetCommandAsync(commandId, ct).ConfigureAwait(false);
            if (cmd is null) return Results.NotFound();
            return Results.Ok(new CommandResultDto(cmd.CommandId, cmd.CompletedAtUtc is not null, cmd.Succeeded, cmd.ResultJson, cmd.Error, cmd.AttemptCount, cmd.LeaseExpiresAtUtc));
        });

        groups.Read.MapGet("/transfer-telemetry", (TransferTelemetryRegistry registry) => Results.Ok(registry.Snapshot()));

        groups.Read.MapGet("/backup-history", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
        {
            var commands = await store.GetRecentCommandsAsync(Math.Clamp(limit ?? 100, 1, 500), ct).ConfigureAwait(false);
            var rows = commands
                .Where(c => c.Type == AgentCommandType.BackupPath)
                .OrderByDescending(c => c.CreatedAtUtc)
                .Select(c =>
                {
                    BackupResultDto? result = null;
                    BackupPayload? request = null;
                    if (!string.IsNullOrWhiteSpace(c.PayloadJson))
                    {
                        try { request = JsonSerializer.Deserialize<BackupPayload>(c.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                        catch (JsonException) { }
                    }
                    if (c.Succeeded && !string.IsNullOrWhiteSpace(c.ResultJson))
                    {
                        try { result = JsonSerializer.Deserialize<BackupResultDto>(c.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                        catch (JsonException) { }
                    }
                    return new
                    {
                        c.CommandId, c.AgentId, c.CreatedAtUtc, c.CompletedAtUtc, c.Succeeded, c.Error,
                        errorCategory = ClassifyBackupError(c.Error),
                        sourcePath = request?.Path,
                        repositoryRoot = request?.RepositoryRoot,
                        repositoryId = request?.RepositoryId,
                        backupId = result?.BackupId, fileCount = result?.FileCount, logicalBytes = result?.LogicalBytes, uploadedBytes = result?.UploadedBytes,
                        newChunks = result?.NewChunks, reusedChunks = result?.ReusedChunks, reusedFiles = result?.ReusedFiles,
                        snapshotBacked = result?.SnapshotBacked, incrementalMode = result?.IncrementalMode, encryptionKeyId = result?.EncryptionKeyId
                    };
                })
                .ToArray();
            return Results.Ok(rows);
        });

        groups.Read.MapGet("/dashboard", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
            var commands = await store.GetRecentCommandsAsync(500, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var onlineCutoff = now.AddMinutes(-3);
            return Results.Ok(new DashboardSummaryDto(
                agents.Count,
                agents.Count(a => a.LastSeenUtc >= onlineCutoff),
                agents.Count(a => a.LastSeenUtc < onlineCutoff),
                policies.Count(p => p.Enabled),
                policies.Count(p => !p.Enabled),
                commands.Count(c => c.CompletedAtUtc is null),
                commands.Count(c => c.CompletedAtUtc >= now.AddHours(-24) && !c.Succeeded),
                agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase)),
                now));
        });

        groups.Read.MapGet("/fleet-intelligence", async (FleetBackupIntelligenceService intelligence, CancellationToken ct) =>
            Results.Ok(await intelligence.BuildAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/operational-health", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var onlineCutoff = now.AddMinutes(-3);
            var metrics = await store.GetOperationalCommandMetricsAsync(now.AddHours(-24), now.AddDays(-30), ct).ConfigureAwait(false);
            var successfulBackups = metrics.SuccessfulBackups;
            var failedBackups = metrics.FailedBackups;
            var successfulDrills = metrics.SuccessfulRestoreDrills;
            var failedDrills = metrics.FailedRestoreDrills;
            var enabledPolicies = policies.Where(p => p.Enabled).ToArray();
            var overduePolicies = enabledPolicies.Count(p => p.NextRunAtUtc < now.AddMinutes(-Math.Max(10, p.IntervalMinutes)));
            var lockedAgents = agents.Count(a => string.Equals(a.ProtectionStatus, "locked", StringComparison.OrdinalIgnoreCase));
            var offlineAgents = agents.Count(a => a.LastSeenUtc < onlineCutoff);

            var score = 100;
            if (agents.Count > 0) score -= (int)Math.Round(25d * offlineAgents / agents.Count);
            if (enabledPolicies.Length > 0) score -= (int)Math.Round(20d * overduePolicies / enabledPolicies.Length);
            var backupAttempts = successfulBackups + failedBackups;
            if (backupAttempts > 0) score -= (int)Math.Round(25d * failedBackups / backupAttempts);
            else if (enabledPolicies.Length > 0) score -= 10;
            var drillAttempts = successfulDrills + failedDrills;
            if (drillAttempts > 0) score -= (int)Math.Round(20d * failedDrills / drillAttempts);
            else if (enabledPolicies.Length > 0) score -= 10;
            if (lockedAgents > 0) score -= Math.Min(20, 10 + lockedAgents);
            score = Math.Clamp(score, 0, 100);
            var grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "E";

            return Results.Ok(new OperationalHealthDto(score, grade, successfulBackups, failedBackups, successfulDrills, failedDrills, overduePolicies, lockedAgents, offlineAgents, now));
        });

        groups.Read.MapGet("/commands", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
        {
            var commands = await store.GetRecentCommandsAsync(Math.Clamp(limit ?? 100, 1, 500), ct).ConfigureAwait(false);
            return Results.Ok(commands.Select(c => new
            {
                c.CommandId, c.AgentId, type = c.Type.ToString(), c.CreatedAtUtc, c.CompletedAtUtc, c.Succeeded, c.Error, c.AttemptCount
            }));
        });

        groups.Security.MapGet("/audit", async (int? limit, IControlPlaneStore store, CancellationToken ct) =>
        {
            var events = await store.GetAuditEventsAsync(Math.Clamp(limit ?? 100, 1, 1000), ct).ConfigureAwait(false);
            return Results.Ok(events.Select(a => new AuditEventDto(a.EventId, a.OccurredAtUtc, a.Actor, a.Action, a.Method, a.Path, a.StatusCode, a.RemoteAddress, a.CorrelationId)));
        });

        groups.Read.MapGet("/cluster", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var nodeId = Environment.GetEnvironmentVariable("YAZMABACKUP_NODE_ID") ?? Environment.MachineName;
            var lease = await store.GetClusterLeaseAsync("policy-scheduler", ct).ConfigureAwait(false);
            var isLeader = lease is not null && lease.ExpiresAtUtc > DateTimeOffset.UtcNow && string.Equals(lease.OwnerId, nodeId, StringComparison.Ordinal);
            return Results.Ok(new ClusterStatusDto(nodeId, isLeader, lease?.OwnerId, lease?.Epoch, lease?.ExpiresAtUtc, DateTimeOffset.UtcNow));
        });

        return groups;
    }
}
