using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using YazmaBackup.ControlPlane;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class ManagementAuthorizationTests
{
    [Theory]
    [InlineData(ManagementRoles.Viewer, ManagementPermissions.Read, true)]
    [InlineData(ManagementRoles.Viewer, ManagementPermissions.Operate, false)]
    [InlineData(ManagementRoles.Viewer, ManagementPermissions.BackupAdministration, false)]
    [InlineData(ManagementRoles.Viewer, ManagementPermissions.SecurityAdministration, false)]
    [InlineData(ManagementRoles.Operator, ManagementPermissions.Read, true)]
    [InlineData(ManagementRoles.Operator, ManagementPermissions.Operate, true)]
    [InlineData(ManagementRoles.Operator, ManagementPermissions.BackupAdministration, false)]
    [InlineData(ManagementRoles.Operator, ManagementPermissions.SecurityAdministration, false)]
    [InlineData(ManagementRoles.BackupAdministrator, ManagementPermissions.Read, true)]
    [InlineData(ManagementRoles.BackupAdministrator, ManagementPermissions.Operate, true)]
    [InlineData(ManagementRoles.BackupAdministrator, ManagementPermissions.BackupAdministration, true)]
    [InlineData(ManagementRoles.BackupAdministrator, ManagementPermissions.SecurityAdministration, false)]
    [InlineData(ManagementRoles.SecurityAdministrator, ManagementPermissions.Read, true)]
    [InlineData(ManagementRoles.SecurityAdministrator, ManagementPermissions.Operate, false)]
    [InlineData(ManagementRoles.SecurityAdministrator, ManagementPermissions.BackupAdministration, false)]
    [InlineData(ManagementRoles.SecurityAdministrator, ManagementPermissions.SecurityAdministration, true)]
    public void Role_permission_matrix_is_fail_closed(string role, string permission, bool expected)
    {
        Assert.Equal(expected, ManagementAuthorization.HasPermission(Principal(role), permission));
    }

    [Theory]
    [InlineData(ManagementPermissions.Read)]
    [InlineData(ManagementPermissions.Operate)]
    [InlineData(ManagementPermissions.BackupAdministration)]
    [InlineData(ManagementPermissions.SecurityAdministration)]
    public void Administrator_has_all_declared_permissions(string permission)
    {
        Assert.True(ManagementAuthorization.HasPermission(Principal(ManagementRoles.Administrator), permission));
    }

    [Fact]
    public void Unknown_permission_is_denied_even_for_non_admin_privileged_role()
    {
        Assert.False(ManagementAuthorization.HasPermission(Principal(ManagementRoles.SecurityAdministrator), "unknown-permission"));
    }

    [Fact]
    public void Api_token_principal_uses_dedicated_authentication_type_and_preserves_roles()
    {
        var token = new ManagementApiTokenRecord(
            Guid.NewGuid(),
            "automation",
            "hash",
            [ManagementRoles.BackupAdministrator],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            false);

        var principal = ManagementAuthorization.CreateApiTokenPrincipal(token);

        Assert.Equal(ManagementAuthorization.ApiTokenAuthenticationType, principal.Identity?.AuthenticationType);
        Assert.True(ManagementAuthorization.HasPermission(principal, ManagementPermissions.BackupAdministration));
        Assert.False(ManagementAuthorization.IsInteractiveAdministrator(principal));
    }

    [Fact]
    public async Task Admin_filter_blocks_identity_that_must_change_password()
    {
        var http = new DefaultHttpContext();
        http.User = Principal(ManagementRoles.Administrator, mustChangePassword: true);
        var invoked = false;
        var filter = new AdminAuthenticationFilter(string.Empty, legacyAdminKeyEnabled: false);

        var result = await filter.InvokeAsync(new TestInvocationContext(http), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(Results.NoContent());
        });

        Assert.False(invoked);
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task Admin_filter_allows_authenticated_identity_after_password_change_gate_clears()
    {
        var http = new DefaultHttpContext();
        http.User = Principal(ManagementRoles.Administrator, mustChangePassword: false);
        var invoked = false;
        var filter = new AdminAuthenticationFilter(string.Empty, legacyAdminKeyEnabled: false);

        await filter.InvokeAsync(new TestInvocationContext(http), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(Results.NoContent());
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task Admin_filter_rejects_anonymous_identity()
    {
        var http = new DefaultHttpContext();
        var invoked = false;
        var filter = new AdminAuthenticationFilter(string.Empty, legacyAdminKeyEnabled: false);

        var result = await filter.InvokeAsync(new TestInvocationContext(http), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(Results.NoContent());
        });

        Assert.False(invoked);
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusResult.StatusCode);
    }

    private static ClaimsPrincipal Principal(string role, bool mustChangePassword = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "tester"),
            new(ClaimTypes.Role, role),
            new("must_change_password", mustChangePassword ? "true" : "false")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private sealed class TestInvocationContext(HttpContext http) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
