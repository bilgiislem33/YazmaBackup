using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class ManagementIdentityEndpoints
{
    internal static AdminEndpointGroups MapManagementIdentityEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Security.MapPost("/enrollment-tokens", async (CreateEnrollmentTokenRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (request.ValidForMinutes is < 1 or > 1440 || request.MaxUses is < 1 or > 5000)
                return Results.BadRequest(new { error = "validForMinutes must be 1..1440 and maxUses must be 1..5000." });
            var (grant, token) = await store.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(request.ValidForMinutes), request.MaxUses, ct).ConfigureAwait(false);
            return Results.Ok(new CreateEnrollmentTokenResponse(grant.GrantId, token, grant.ExpiresAtUtc, request.MaxUses));
        });

        groups.Security.MapGet("/users", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var users = await store.GetManagementUsersAsync(ct).ConfigureAwait(false);
            return Results.Ok(users.Select(ToManagementUserDto));
        });

        groups.Security.MapPost("/users", async (HttpContext http, CreateManagementUserRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (request.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase) && !ManagementAuthorization.IsInteractiveAdministrator(http.User))
                return Results.Forbid();
            try
            {
                var user = await store.CreateManagementUserAsync(request.Username, request.DisplayName, request.Password, request.Roles, request.MustChangePassword, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/users/{user.UserId}", ToManagementUserDto(user));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Security.MapPost("/users/{userId:guid}/enabled", async (Guid userId, HttpContext http, SetManagementUserEnabledRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var target = (await store.GetManagementUsersAsync(ct).ConfigureAwait(false)).FirstOrDefault(u => u.UserId == userId);
            if (target is null) return Results.NotFound();
            if (target.Roles.Contains(ManagementRoles.Administrator, StringComparer.OrdinalIgnoreCase) && !ManagementAuthorization.IsInteractiveAdministrator(http.User))
                return Results.Forbid();
            try
            {
                var user = await store.SetManagementUserEnabledAsync(userId, request.Enabled, ct).ConfigureAwait(false);
                return user is null ? Results.NotFound() : Results.Ok(ToManagementUserDto(user));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        groups.Security.MapGet("/api-tokens", async (IControlPlaneStore store, CancellationToken ct) =>
        {
            var tokens = await store.GetManagementApiTokensAsync(ct).ConfigureAwait(false);
            return Results.Ok(tokens.Select(t => new ManagementApiTokenDto(t.TokenId, t.Name, t.Roles, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.Revoked)));
        });

        groups.Security.MapPost("/api-tokens", async (HttpContext http, CreateManagementApiTokenRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ManagementAuthorization.IsInteractiveAdministrator(http.User)) return Results.Forbid();
            try
            {
                var validity = TimeSpan.FromHours(Math.Clamp(request.ValidForHours, 1, 2160));
                var issued = await store.CreateManagementApiTokenAsync(request.Name, request.Roles, validity, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/admin/api-tokens/{issued.Record.TokenId}", new CreateManagementApiTokenResponse(issued.Record.TokenId, issued.PlaintextToken, issued.Record.Name, issued.Record.Roles, issued.Record.ExpiresAtUtc));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Security.MapDelete("/api-tokens/{tokenId:guid}", async (Guid tokenId, HttpContext http, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ManagementAuthorization.IsInteractiveAdministrator(http.User)) return Results.Forbid();
            return await store.RevokeManagementApiTokenAsync(tokenId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
        });

        groups.Security.MapPost("/users/{userId:guid}/break-glass-codes", async (Guid userId, HttpContext http, CreateBreakGlassCodesRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            var currentUserId = ManagementAuthorization.GetUserId(http.User);
            if (!ManagementAuthorization.IsInteractiveAdministrator(http.User) || currentUserId != userId) return Results.Forbid();
            try
            {
                var count = Math.Clamp(request.Count, 1, 20);
                var days = Math.Clamp(request.ValidForDays, 1, 90);
                var codes = await store.CreateBreakGlassRecoveryCodesAsync(userId, count, TimeSpan.FromDays(days), ct).ConfigureAwait(false);
                return Results.Ok(new CreateBreakGlassCodesResponse(DateTimeOffset.UtcNow.AddDays(days), codes));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        return groups;
    }
}
