using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using YazmaBackup.Contracts;

namespace YazmaBackup.ControlPlane.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints, bool allowInsecureUiCookie)
    {
        endpoints.MapPost("/api/v1/session/login", async (HttpContext http, LoginRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidText(request.Username, 64) || string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
                return Results.Unauthorized();

            ManagementUserRecord? user;
            try { user = await store.AuthenticateManagementUserAsync(request.Username, request.Password, ct).ConfigureAwait(false); }
            catch (ArgumentException) { user = null; }
            if (user is null)
            {
                await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Username.Trim(), "login-failed", "POST", "/api/v1/session/login", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
                return Results.Unauthorized();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.Username),
                new("display_name", user.DisplayName),
                new("must_change_password", user.MustChangePassword ? "true" : "false")
            };
            claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                AllowRefresh = true
            }).ConfigureAwait(false);

            var csrf = NewCsrfToken();
            http.Response.Cookies.Append("yb-csrf", csrf, SessionCookieOptions(allowInsecureUiCookie, TimeSpan.FromHours(8)));
            await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "login-success", "POST", "/api/v1/session/login", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
            return Results.Ok(new LoginResponse(ToSessionUser(user), csrf));
        }).RequireRateLimiting("login");

        endpoints.MapGet("/api/v1/session/oidc/status", (OidcClient oidc) => Results.Ok(new { enabled = oidc.Enabled }));

        endpoints.MapGet("/api/v1/session/oidc/start", async (HttpContext http, OidcClient oidc, IDataProtectionProvider dataProtection, CancellationToken ct) =>
        {
            if (!oidc.Enabled) return Results.NotFound();
            var transaction = new OidcLoginTransaction(
                OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                OidcClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                DateTimeOffset.UtcNow);
            var protector = dataProtection.CreateProtector("YazmaBackup.OIDC.Transaction.v1");
            var protectedValue = protector.Protect(JsonSerializer.Serialize(transaction));
            http.Response.Cookies.Append("yb-oidc-tx", protectedValue, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !allowInsecureUiCookie,
                Path = "/",
                MaxAge = TimeSpan.FromMinutes(10),
                IsEssential = true
            });
            var authorizationUri = await oidc.BuildAuthorizationUriAsync(transaction, ct).ConfigureAwait(false);
            return Results.Redirect(authorizationUri.ToString());
        }).RequireRateLimiting("login");

        endpoints.MapGet("/api/v1/session/oidc/callback", async (HttpContext http, OidcClient oidc, IDataProtectionProvider dataProtection, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!oidc.Enabled) return Results.NotFound();
            var code = http.Request.Query["code"].FirstOrDefault();
            var state = http.Request.Query["state"].FirstOrDefault();
            var protectedValue = http.Request.Cookies["yb-oidc-tx"];
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(protectedValue)) return Results.Unauthorized();

            try
            {
                var protector = dataProtection.CreateProtector("YazmaBackup.OIDC.Transaction.v1");
                var transaction = JsonSerializer.Deserialize<OidcLoginTransaction>(protector.Unprotect(protectedValue));
                if (transaction is null || !Security.ConstantTimeEquals(state, transaction.State) || DateTimeOffset.UtcNow - transaction.CreatedAtUtc > TimeSpan.FromMinutes(10)) return Results.Unauthorized();
                var identity = await oidc.ExchangeAndValidateAsync(code, transaction, ct).ConfigureAwait(false);
                var user = await store.FindOrProvisionExternalUserAsync(identity.Issuer, identity.Subject, identity.Username, identity.DisplayName, identity.Roles, ct).ConfigureAwait(false);
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                    new(ClaimTypes.Name, user.Username),
                    new("display_name", user.DisplayName),
                    new("must_change_password", "false"),
                    new("auth_source", "oidc")
                };
                claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                    AllowRefresh = true
                }).ConfigureAwait(false);
                var csrf = NewCsrfToken();
                http.Response.Cookies.Append("yb-csrf", csrf, SessionCookieOptions(allowInsecureUiCookie, TimeSpan.FromHours(8)));
                http.Response.Cookies.Delete("yb-oidc-tx", new CookieOptions { Path = "/", Secure = !allowInsecureUiCookie, SameSite = SameSiteMode.Lax });
                await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "oidc-login", "GET", "/api/v1/session/oidc/callback", 302, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
                return Results.Redirect("/");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or CryptographicException or JsonException or HttpRequestException)
            {
                await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, "oidc", "oidc-login-failed", "GET", "/api/v1/session/oidc/callback", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
                return Results.Unauthorized();
            }
        }).RequireRateLimiting("login");

        endpoints.MapPost("/api/v1/session/break-glass", async (HttpContext http, BreakGlassLoginRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (!ValidText(request.Username, 64) || !ValidText(request.RecoveryCode, 64)) return Results.Unauthorized();
            var user = await store.RedeemBreakGlassRecoveryCodeAsync(request.Username, request.RecoveryCode, ct).ConfigureAwait(false);
            if (user is null)
            {
                await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Username.Trim(), "break-glass-failed", "POST", "/api/v1/session/break-glass", 401, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
                return Results.Unauthorized();
            }
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.Username),
                new("display_name", user.DisplayName),
                new("must_change_password", "true"),
                new("break_glass", "true")
            };
            claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                AllowRefresh = false
            }).ConfigureAwait(false);
            var csrf = NewCsrfToken();
            http.Response.Cookies.Append("yb-csrf", csrf, SessionCookieOptions(allowInsecureUiCookie, TimeSpan.FromMinutes(30)));
            await store.AppendAuditEventAsync(new AuditEventRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Username, "break-glass-login", "POST", "/api/v1/session/break-glass", 200, http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier), ct).ConfigureAwait(false);
            return Results.Ok(new LoginResponse(ToSessionUser(user), csrf));
        }).RequireRateLimiting("login");

        endpoints.MapGet("/api/v1/session/me", (HttpContext http) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var userId = ManagementAuthorization.GetUserId(http.User);
            if (userId is null) return Results.Unauthorized();
            var roles = http.User.FindAll(ClaimTypes.Role).Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var mustChange = string.Equals(http.User.FindFirst("must_change_password")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new SessionUserDto(userId.Value, http.User.Identity.Name ?? string.Empty, http.User.FindFirst("display_name")?.Value ?? http.User.Identity.Name ?? string.Empty, roles, mustChange));
        });

        endpoints.MapPost("/api/v1/session/logout", async (HttpContext http) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.NoContent();
            if (!ValidCsrf(http)) return Results.BadRequest(new { error = "CSRF validation failed." });
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            http.Response.Cookies.Delete("yb-csrf", new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        endpoints.MapPost("/api/v1/session/change-password", async (HttpContext http, ChangeOwnPasswordRequest request, IControlPlaneStore store, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || !ValidCsrf(http)) return Results.Unauthorized();
            var userId = ManagementAuthorization.GetUserId(http.User);
            if (userId is null) return Results.Unauthorized();
            try
            {
                var breakGlass = string.Equals(http.User.FindFirst("break_glass")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                var changed = breakGlass
                    ? await store.ResetManagementPasswordAfterBreakGlassAsync(userId.Value, request.NewPassword, ct).ConfigureAwait(false)
                    : await store.ChangeManagementPasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword, ct).ConfigureAwait(false);
                if (!changed) return Results.Unauthorized();
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                http.Response.Cookies.Delete("yb-csrf", new CookieOptions { Path = "/" });
                return Results.NoContent();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return endpoints;
    }

    internal static bool ValidCsrf(HttpContext http)
    {
        var cookie = http.Request.Cookies["yb-csrf"];
        var header = http.Request.Headers["X-YazmaBackup-CSRF"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(cookie) && !string.IsNullOrWhiteSpace(header) && Security.ConstantTimeEquals(header, cookie);
    }

    private static SessionUserDto ToSessionUser(ManagementUserRecord user) =>
        new(user.UserId, user.Username, user.DisplayName, user.Roles, user.MustChangePassword);

    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    private static string NewCsrfToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static CookieOptions SessionCookieOptions(bool allowInsecureUiCookie, TimeSpan maxAge) => new()
    {
        HttpOnly = false,
        SameSite = SameSiteMode.Strict,
        Secure = !allowInsecureUiCookie,
        Path = "/",
        MaxAge = maxAge
    };
}
