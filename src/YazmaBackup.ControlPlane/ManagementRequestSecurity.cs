using Microsoft.AspNetCore.Authentication.Cookies;

namespace YazmaBackup.ControlPlane;

internal static class ManagementRequestSecurity
{
    internal const string CsrfCookieName = "yb-csrf";
    internal const string CsrfHeaderName = "X-YazmaBackup-CSRF";
    internal const string LegacyAdminKeyHeaderName = "X-YazmaBackup-Admin-Key";

    internal static bool IsAdminMutation(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/v1/admin")
        && !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method);

    internal static bool UsesCookieIdentity(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal);

    internal static bool UsesApiTokenIdentity(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, ManagementAuthorization.ApiTokenAuthenticationType, StringComparison.Ordinal);

    internal static bool UsesLegacyAdminKey(HttpRequest request, string adminKey, bool legacyAdminKeyEnabled) =>
        legacyAdminKeyEnabled
        && Security.ConstantTimeEquals(request.Headers[LegacyAdminKeyHeaderName].FirstOrDefault(), adminKey);

    internal static bool HasValidCsrf(HttpRequest request)
    {
        var cookie = request.Cookies[CsrfCookieName];
        var header = request.Headers[CsrfHeaderName].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(cookie)
            && !string.IsNullOrWhiteSpace(header)
            && Security.ConstantTimeEquals(cookie, header);
    }
}
