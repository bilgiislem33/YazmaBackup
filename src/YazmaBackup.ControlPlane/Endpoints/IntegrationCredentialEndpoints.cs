using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class IntegrationCredentialEndpoints
{
    internal static AdminEndpointGroups MapIntegrationCredentialEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Security.MapPost("/integration-credentials", async (CreateIntegrationCredentialRequest request, HttpContext http, IProductionFabricStore store, CancellationToken ct) =>
        {
            if (!string.Equals(http.User.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal)) return Results.Forbid();
            try
            {
                var days = Math.Clamp(request.ValidForDays, 1, 365);
                var created = await store.CreateIntegrationCredentialAsync(request.Name, request.Purpose, TimeSpan.FromDays(days), ct).ConfigureAwait(false);
                return Results.Ok(new CreateIntegrationCredentialResponse(created.Record.CredentialId, created.PlaintextToken, created.Record.Name, created.Record.Purpose, created.Record.ExpiresAtUtc));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Security.MapGet("/integration-credentials", async (IProductionFabricStore store, CancellationToken ct) =>
        {
            var items = await store.GetIntegrationCredentialsAsync(ct).ConfigureAwait(false);
            return Results.Ok(items.Select(x => new IntegrationCredentialDto(x.CredentialId, x.Name, x.Purpose, x.CreatedAtUtc, x.ExpiresAtUtc, x.LastUsedAtUtc, x.Revoked)));
        });

        groups.Security.MapDelete("/integration-credentials/{credentialId:guid}", async (Guid credentialId, HttpContext http, IProductionFabricStore store, CancellationToken ct) =>
        {
            if (!string.Equals(http.User.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal)) return Results.Forbid();
            return await store.RevokeIntegrationCredentialAsync(credentialId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
        });

        return groups;
    }
}
