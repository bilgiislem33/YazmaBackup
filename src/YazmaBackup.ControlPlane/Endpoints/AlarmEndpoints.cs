using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AlarmEndpoints
{
    internal static AdminEndpointGroups MapAlarmEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet("/alarms", async (int? limit, bool? includeResolved, IControlPlaneStore store, CancellationToken ct) =>
        {
            var alarms = await store.GetAlarmsAsync(Math.Clamp(limit ?? 200, 1, 1000), includeResolved ?? false, ct).ConfigureAwait(false);
            return Results.Ok(alarms.Select(ToAlarmDto));
        });

        groups.Read.MapGet("/alarms/summary", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var alarms = await store.GetAlarmsAsync(2000, true, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new AlarmSummaryDto(
                alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Critical),
                alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Warning),
                alarms.Count(a => a.Status != AlarmStatus.Resolved && a.Severity == AlarmSeverity.Info),
                alarms.Count(a => a.Status == AlarmStatus.Acknowledged),
                alarms.Count(a => a.Status == AlarmStatus.Resolved), now));
        });

        groups.Operate.MapPost("/alarms/{alarmId:guid}/acknowledge", async (Guid alarmId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
            var alarm = await store.AcknowledgeAlarmAsync(alarmId, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return alarm is null ? Results.NotFound() : Results.Ok(ToAlarmDto(alarm));
        });

        groups.Operate.MapPost("/alarms/{alarmId:guid}/workflow", async (Guid alarmId, HttpContext http, UpdateAlarmWorkflowRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
            try
            {
                var alarm = await store.UpdateAlarmWorkflowAsync(alarmId, request.AssignedTo, request.Note, request.DueAtUtc, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                return alarm is null ? Results.NotFound() : Results.Ok(ToAlarmDto(alarm));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Operate.MapPost("/alarms/bulk", async (HttpContext http, BulkAlarmActionRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (request.AlarmIds is null || request.AlarmIds.Count is < 1 or > 500) return Results.BadRequest(new { error = "alarmIds must contain 1..500 items." });
            var ids = request.AlarmIds.Distinct().ToArray();
            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action is not ("acknowledge" or "resolve" or "assign" or "note" or "workflow")) return Results.BadRequest(new { error = "Unsupported bulk alarm action." });
            var actor = ManagementAuthorization.Actor(http, adminKey, legacyAdminKeyEnabled);
            var updated = new List<AlarmDto>();
            foreach (var id in ids)
            {
                AlarmRecord? alarm = action switch
                {
                    "acknowledge" => await store.AcknowledgeAlarmAsync(id, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false),
                    "resolve" => await store.ResolveAlarmByIdAsync(id, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false),
                    _ => await store.UpdateAlarmWorkflowAsync(id, request.AssignedTo, request.Note, request.DueAtUtc, actor, DateTimeOffset.UtcNow, ct).ConfigureAwait(false)
                };
                if (alarm is not null) updated.Add(ToAlarmDto(alarm));
            }
            return Results.Ok(updated);
        });

        return groups;
    }
}
