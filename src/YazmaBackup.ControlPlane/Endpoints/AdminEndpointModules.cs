namespace YazmaBackup.ControlPlane.Endpoints;

internal static class AdminEndpointModules
{
    internal static AdminEndpointGroups MapAdminEndpointModules(
        this AdminEndpointGroups groups,
        string stateEngine)
    {
        ArgumentNullException.ThrowIfNull(groups);

        groups.MapHighAvailabilityEndpoints(stateEngine);
        groups.MapNasStorageEndpoints();
        groups.MapRepositoryKeyEndpoints();
        groups.MapBackupOperationEndpoints();
        groups.MapRestoreOperationEndpoints();
        return groups;
    }
}
