using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;
using static YazmaBackup.ControlPlane.Endpoints.EndpointSupport;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class SettingsTransferEndpoints
{
    internal static AdminEndpointGroups MapSettingsTransferEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Security.MapPost("/settings/export", async (SettingsExportRequest request, SettingsTransferService transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.ExportAsync(request.Passphrase, ct).ConfigureAwait(false)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        groups.Security.MapPost("/settings/import", async (SettingsImportRequest request, SettingsTransferService transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.ImportAsync(request.Passphrase, request.BundleBase64, ct).ConfigureAwait(false)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return groups;
    }
}
