using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class BackupPolicyEndpoints
{
    internal static AdminEndpointGroups MapBackupPolicyEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/pilot/readiness", async (PilotReadinessService pilot, CancellationToken ct) =>
            Results.Ok(await pilot.BuildSummaryAsync(ct).ConfigureAwait(false)));

        groups.Read.MapGet("/policy-templates", (PilotReadinessService pilot) => Results.Ok(pilot.GetTemplates()));

        groups.Backup.MapPost("/policy-templates/{templateId}/apply", async (string templateId, ApplyPolicyTemplateRequest request, PilotReadinessService pilot, IControlPlaneStore store, CancellationToken ct) =>
        {
            var template = pilot.FindTemplate(templateId);
            if (template is null) return Results.NotFound(new { error = "Policy template not found." });
            if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });
            var name = string.IsNullOrWhiteSpace(request.PolicyName) ? $"{template.Name} · {request.SourcePath}" : request.PolicyName.Trim();
            if (!ValidText(name, 128)) return Results.BadRequest(new { error = "Policy name is invalid." });
            var now = DateTimeOffset.UtcNow;
            var policy = new BackupPolicyRecord(
                Guid.NewGuid(), name, request.AgentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId, request.RequireSnapshot,
                template.IntervalMinutes, template.ActiveBytesPerSecond, template.IdleBytesPerSecond, 300, template.Retention, request.Enabled, now, null, now, template.Protection,
                template.RestoreDrillIntervalDays, null, now.AddDays(template.RestoreDrillIntervalDays), template.RepositoryHealthIntervalHours, null, now);
            try
            {
                var created = await store.CreateBackupPolicyAsync(policy, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/policies/{created.PolicyId}", ToPolicyDto(created));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Agent not found." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/policy-templates/{templateId}/apply-bulk", async (string templateId, BulkApplyPolicyTemplateRequest request, PilotReadinessService pilot, IControlPlaneStore store, CancellationToken ct) =>
        {
            var template = pilot.FindTemplate(templateId);
            if (template is null) return Results.NotFound(new { error = "Policy template not found." });
            if (request.AgentIds is null || request.AgentIds.Count is < 1 or > 500 || request.AgentIds.Distinct().Count() != request.AgentIds.Count)
                return Results.BadRequest(new { error = "AgentIds must contain 1..500 unique identifiers." });
            if (!ValidPathInput(request.SourcePath) || !ValidPathInput(request.RepositoryRoot) || !ValidIdentifier(request.RepositoryId, 128))
                return Results.BadRequest(new { error = "Source path, repository root or repository id is invalid." });

            var now = DateTimeOffset.UtcNow;
            var prefix = string.IsNullOrWhiteSpace(request.PolicyNamePrefix) ? template.Name : request.PolicyNamePrefix.Trim();
            if (!ValidText(prefix, 96)) return Results.BadRequest(new { error = "Policy name prefix is invalid." });
            var agents = await store.GetAgentsAsync(ct).ConfigureAwait(false);
            var map = agents.ToDictionary(x => x.AgentId);
            if (request.AgentIds.Any(id => !map.ContainsKey(id))) return Results.NotFound(new { error = "One or more Agents were not found. No policies were created." });

            var policies = request.AgentIds.Select(agentId =>
            {
                var machine = map[agentId].MachineName;
                var name = $"{prefix} · {machine}";
                if (name.Length > 128) name = name[..128];
                return new BackupPolicyRecord(Guid.NewGuid(), name, agentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId,
                    request.RequireSnapshot, template.IntervalMinutes, template.ActiveBytesPerSecond, template.IdleBytesPerSecond, 300,
                    template.Retention, request.Enabled, now, null, now, template.Protection,
                    template.RestoreDrillIntervalDays, null, now.AddDays(template.RestoreDrillIntervalDays), template.RepositoryHealthIntervalHours, null, now);
            }).ToArray();

            try
            {
                var created = await store.CreateBackupPoliciesAsync(policies, ct).ConfigureAwait(false);
                return Results.Ok(new BulkApplyPolicyTemplateResultDto(template.TemplateId, created.Count, created.Select(ToPolicyDto).ToArray()));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/policies", async (CreateBackupPolicyRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var retention = request.Retention ?? new RetentionPolicy();
            var protection = request.Protection ?? new ProtectionPolicy();
            if (!ValidText(request.Name, 128) || request.IntervalMinutes is < 5 or > 43200 || request.RestoreDrillIntervalDays is < 1 or > 365 || request.RepositoryHealthIntervalHours is < 1 or > 720)
                return Results.BadRequest(new { error = "Policy name, backup interval or restore drill interval is invalid." });
            if (!ValidBackupRequest(request.SourcePath, request.RepositoryRoot, request.RepositoryId, request.ActiveBytesPerSecond, request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, protection, out var error))
                return Results.BadRequest(new { error });
            var now = DateTimeOffset.UtcNow;
            var policy = new BackupPolicyRecord(
                Guid.NewGuid(), request.Name.Trim(), request.AgentId, request.SourcePath, request.RepositoryRoot, request.RepositoryId,
                request.RequireSnapshot, request.IntervalMinutes, request.ActiveBytesPerSecond, request.IdleBytesPerSecond,
                request.UserIdleThresholdSeconds, retention, request.Enabled, now, null, now, protection,
                request.RestoreDrillIntervalDays, null, now.AddDays(request.RestoreDrillIntervalDays),
                request.RepositoryHealthIntervalHours, null, now);
            try
            {
                var created = await store.CreateBackupPolicyAsync(policy, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/policies/{created.PolicyId}", ToPolicyDto(created));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Agent not found." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Backup.MapPost("/policies/multi-source", async (CreateMultiSourceBackupPolicyRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var retention = request.Retention ?? new RetentionPolicy();
            var protection = request.Protection ?? new ProtectionPolicy();
            if (request.SourcePaths is null || request.SourcePaths.Count is < 1 or > 100 ||
                request.SourcePaths.Any(path => !ValidPathInput(path)) ||
                request.SourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.SourcePaths.Count)
                return Results.BadRequest(new { error = "SourcePaths must contain 1..100 unique valid paths." });
            if (!ValidText(request.Name, 110) || request.IntervalMinutes is < 5 or > 43200 || request.RestoreDrillIntervalDays is < 1 or > 365 || request.RepositoryHealthIntervalHours is < 1 or > 720)
                return Results.BadRequest(new { error = "Policy name, backup interval or restore drill interval is invalid." });
            if (!ValidBackupRequest(request.SourcePaths[0], request.RepositoryRoot, request.RepositoryId, request.ActiveBytesPerSecond, request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, protection, out var error))
                return Results.BadRequest(new { error });

            var now = DateTimeOffset.UtcNow;
            var policies = request.SourcePaths.Select((sourcePath, index) => new BackupPolicyRecord(
                Guid.NewGuid(), request.SourcePaths.Count == 1 ? request.Name.Trim() : $"{request.Name.Trim()} · {index + 1}", request.AgentId, sourcePath,
                request.RepositoryRoot, request.RepositoryId, request.RequireSnapshot, request.IntervalMinutes, request.ActiveBytesPerSecond,
                request.IdleBytesPerSecond, request.UserIdleThresholdSeconds, retention, request.Enabled, now, null, now, protection,
                request.RestoreDrillIntervalDays, null, now.AddDays(request.RestoreDrillIntervalDays), request.RepositoryHealthIntervalHours, null, now)).ToArray();
            try
            {
                var created = await store.CreateBackupPoliciesAsync(policies, ct).ConfigureAwait(false);
                return Results.Ok(new CreateMultiSourceBackupPolicyResultDto(created.Count, created.Select(ToPolicyDto).ToArray()));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Agent not found. No policies were created." }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Read.MapGet("/policies", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var policies = await store.GetBackupPoliciesAsync(ct).ConfigureAwait(false);
            return Results.Ok(policies.Select(ToPolicyDto));
        });

        groups.Backup.MapPost("/policies/{policyId:guid}/enabled", async (Guid policyId, SetPolicyEnabledRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var policy = await store.SetBackupPolicyEnabledAsync(policyId, request.Enabled, ct).ConfigureAwait(false);
            return policy is null ? Results.NotFound() : Results.Ok(ToPolicyDto(policy));
        });

        groups.Backup.MapDelete("/policies/{policyId:guid}", async (Guid policyId, IControlPlaneStore store, CancellationToken ct) =>
        {
            return await store.DeleteBackupPolicyAsync(policyId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
        });

        return groups;
    }
}
