using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public static class ManagementPermissions
{
    public const string Read = "read";
    public const string Operate = "operate";
    public const string BackupAdministration = "backup-admin";
    public const string SecurityAdministration = "security-admin";
}

public static class ManagementAuthorization
{
    public const string ApiTokenAuthenticationType = "YazmaBackupApiToken";

    public static bool HasPermission(ClaimsPrincipal principal, string permission)
    {
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roles.Contains(ManagementRoles.Administrator)) return true;
        return permission switch
        {
            ManagementPermissions.Read => roles.Overlaps(new[] { ManagementRoles.Viewer, ManagementRoles.Operator, ManagementRoles.BackupAdministrator, ManagementRoles.SecurityAdministrator }),
            ManagementPermissions.Operate => roles.Overlaps(new[] { ManagementRoles.Operator, ManagementRoles.BackupAdministrator }),
            ManagementPermissions.BackupAdministration => roles.Contains(ManagementRoles.BackupAdministrator),
            ManagementPermissions.SecurityAdministration => roles.Contains(ManagementRoles.SecurityAdministrator),
            _ => false
        };
    }

    public static bool IsAdministrator(ClaimsPrincipal principal) =>
        principal.FindAll(ClaimTypes.Role).Any(c => string.Equals(c.Value, ManagementRoles.Administrator, StringComparison.OrdinalIgnoreCase));

    public static bool IsInteractiveAdministrator(ClaimsPrincipal principal) =>
        string.Equals(principal.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal) && IsAdministrator(principal);

    public static Guid? GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public static string Actor(HttpContext http, string adminKey, bool legacyAdminKeyEnabled)
    {
        if (http.User.Identity?.IsAuthenticated == true)
            return http.User.Identity.Name ?? "management-identity";
        return legacyAdminKeyEnabled && Security.ConstantTimeEquals(http.Request.Headers["X-YazmaBackup-Admin-Key"].FirstOrDefault(), adminKey)
            ? "legacy-admin-api-key"
            : "anonymous";
    }

    public static ClaimsPrincipal CreateApiTokenPrincipal(ManagementApiTokenRecord token)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.TokenId.ToString()),
            new(ClaimTypes.Name, "api-token:" + token.Name),
            new("management_token_id", token.TokenId.ToString())
        };
        claims.AddRange(token.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokenAuthenticationType));
    }
}

public sealed class AdminAuthenticationFilter(string adminKey, bool legacyAdminKeyEnabled) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (legacyAdminKeyEnabled && Security.ConstantTimeEquals(http.Request.Headers["X-YazmaBackup-Admin-Key"].FirstOrDefault(), adminKey))
            return await next(context).ConfigureAwait(false);
        if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        if (string.Equals(http.User.FindFirst("must_change_password")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(context).ConfigureAwait(false);
    }
}

public sealed class PermissionFilter(string adminKey, bool legacyAdminKeyEnabled, string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (legacyAdminKeyEnabled && Security.ConstantTimeEquals(http.Request.Headers["X-YazmaBackup-Admin-Key"].FirstOrDefault(), adminKey))
            return await next(context).ConfigureAwait(false);
        if (ManagementAuthorization.HasPermission(http.User, permission))
            return await next(context).ConfigureAwait(false);
        return http.User.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
    }
}
