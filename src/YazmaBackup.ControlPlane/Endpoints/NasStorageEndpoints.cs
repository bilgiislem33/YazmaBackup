using Microsoft.AspNetCore.Builder;
using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane.Endpoints;

internal static class NasStorageEndpoints
{
    internal static AdminEndpointGroups MapNasStorageEndpoints(this AdminEndpointGroups groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.Read.MapGet(NasStorageEndpointContracts.GlobalProfile, async (
            GlobalNasProfileStore profiles,
            CancellationToken ct) =>
        {
            var profile = await profiles.GetAsync(ct).ConfigureAwait(false);
            return profile is null
                ? Results.NoContent()
                : Results.Ok(GlobalNasProfileStore.ToSummary(profile));
        });

        groups.Security.MapPost(NasStorageEndpointContracts.GlobalProfile, async (
            SaveGlobalNasProfileRequest request,
            GlobalNasProfileStore profiles,
            GlobalNasProfileService profileService,
            CancellationToken ct) =>
        {
            if (!ValidIdentifier(request.RepositoryId, 128) ||
                !ValidPathInput(request.RepositoryRoot) ||
                !ValidText(request.Username, 128))
            {
                return Results.BadRequest(new { error = "Repository, NAS yolu veya kullanıcı adı geçersiz." });
            }

            var existing = await profiles.GetAsync(ct).ConfigureAwait(false);
            var password = request.Password;
            if (string.IsNullOrEmpty(password))
            {
                if (existing is null || string.IsNullOrEmpty(existing.Password))
                    return Results.BadRequest(new { error = "İlk global NAS kaydında parola zorunludur." });
                password = existing.Password;
            }

            if (password.Length > 512)
                return Results.BadRequest(new { error = "NAS parolası çok uzun." });

            var saved = await profiles.SaveAsync(
                request.RepositoryId,
                request.RepositoryRoot,
                request.Username,
                password,
                ct).ConfigureAwait(false);
            var applied = await profileService.ApplyToAllAgentsAsync(saved, ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                profile = GlobalNasProfileStore.ToSummary(saved),
                queuedAgents = applied.Queued,
                skippedAgents = applied.Skipped,
                passwordReused = string.IsNullOrEmpty(request.Password)
            });
        });

        return groups;
    }

    private static bool ValidText(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    private static bool ValidPathInput(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32767;

    private static bool ValidIdentifier(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= max &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
