using Microsoft.AspNetCore.Routing;

namespace YazmaBackup.ControlPlane.Endpoints;

public sealed record AdminEndpointGroups(
    RouteGroupBuilder Root,
    RouteGroupBuilder Read,
    RouteGroupBuilder Operate,
    RouteGroupBuilder Backup,
    RouteGroupBuilder Security,
    string AdminKey,
    bool LegacyAdminKeyEnabled);

public static class AdminEndpointGroupExtensions
{
    public static AdminEndpointGroups CreateAdminEndpointGroups(
        this IEndpointRouteBuilder endpoints,
        string adminKey,
        bool legacyAdminKeyEnabled)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var root = endpoints.MapGroup("/api/v1/admin");
        root.AddEndpointFilter(new AdminAuthenticationFilter(adminKey, legacyAdminKeyEnabled));

        var read = root.MapGroup(string.Empty);
        read.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.Read));

        var operate = root.MapGroup(string.Empty);
        operate.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.Operate));

        var backup = root.MapGroup(string.Empty);
        backup.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.BackupAdministration));

        var security = root.MapGroup(string.Empty);
        security.AddEndpointFilter(new PermissionFilter(adminKey, legacyAdminKeyEnabled, ManagementPermissions.SecurityAdministration));

        return new AdminEndpointGroups(root, read, operate, backup, security, adminKey, legacyAdminKeyEnabled);
    }
}
