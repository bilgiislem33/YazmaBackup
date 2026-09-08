using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using YazmaBackup.ControlPlane;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class ManagementRequestSecurityTests
{
    [Theory]
    [InlineData("POST", "/api/v1/admin/policies", true)]
    [InlineData("PUT", "/api/v1/admin/settings", true)]
    [InlineData("DELETE", "/api/v1/admin/policies/1", true)]
    [InlineData("PATCH", "/api/v1/admin/policies/1", true)]
    [InlineData("GET", "/api/v1/admin/policies", false)]
    [InlineData("HEAD", "/api/v1/admin/policies", false)]
    [InlineData("POST", "/api/v1/session/login", false)]
    public void Admin_mutation_scope_is_exact(string method, string path, bool expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;

        Assert.Equal(expected, ManagementRequestSecurity.IsAdminMutation(http.Request));
    }

    [Fact]
    public void Csrf_requires_matching_cookie_and_header()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "yb-csrf=abc123";
        http.Request.Headers[ManagementRequestSecurity.CsrfHeaderName] = "abc123";

        Assert.True(ManagementRequestSecurity.HasValidCsrf(http.Request));
    }

    [Theory]
    [InlineData("", "abc123")]
    [InlineData("abc123", "")]
    [InlineData("abc123", "different")]
    public void Csrf_fails_closed_when_missing_or_mismatched(string cookie, string header)
    {
        var http = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(cookie)) http.Request.Headers.Cookie = $"yb-csrf={cookie}";
        if (!string.IsNullOrEmpty(header)) http.Request.Headers[ManagementRequestSecurity.CsrfHeaderName] = header;

        Assert.False(ManagementRequestSecurity.HasValidCsrf(http.Request));
    }

    [Fact]
    public void Cookie_identity_is_recognized_only_by_cookie_authentication_type()
    {
        var cookiePrincipal = Principal(CookieAuthenticationDefaults.AuthenticationScheme);
        var bearerPrincipal = Principal("Bearer");

        Assert.True(ManagementRequestSecurity.UsesCookieIdentity(cookiePrincipal));
        Assert.False(ManagementRequestSecurity.UsesCookieIdentity(bearerPrincipal));
    }

    [Fact]
    public void Management_api_token_identity_is_recognized_without_cookie_identity()
    {
        var principal = Principal(ManagementAuthorization.ApiTokenAuthenticationType);

        Assert.True(ManagementRequestSecurity.UsesApiTokenIdentity(principal));
        Assert.False(ManagementRequestSecurity.UsesCookieIdentity(principal));
    }

    [Fact]
    public void Legacy_admin_key_is_disabled_unless_feature_flag_is_enabled()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[ManagementRequestSecurity.LegacyAdminKeyHeaderName] = "secret";

        Assert.False(ManagementRequestSecurity.UsesLegacyAdminKey(http.Request, "secret", legacyAdminKeyEnabled: false));
        Assert.True(ManagementRequestSecurity.UsesLegacyAdminKey(http.Request, "secret", legacyAdminKeyEnabled: true));
    }

    [Fact]
    public void Legacy_admin_key_mismatch_fails_closed()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[ManagementRequestSecurity.LegacyAdminKeyHeaderName] = "wrong";

        Assert.False(ManagementRequestSecurity.UsesLegacyAdminKey(http.Request, "expected", legacyAdminKeyEnabled: true));
    }

    private static ClaimsPrincipal Principal(string authenticationType) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], authenticationType));
}
