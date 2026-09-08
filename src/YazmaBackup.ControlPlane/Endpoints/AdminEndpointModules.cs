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
        groups.MapFleetMonitoringEndpoints();
        groups.MapAgentMaintenanceEndpoints();
        groups.MapBackupPolicyEndpoints();
        groups.MapManagementIdentityEndpoints();
        groups.MapSettingsTransferEndpoints();
        groups.MapAutonomousProtectionEndpoints();
        groups.MapRecoveryManagementEndpoints();
        groups.MapBusinessContinuityEndpoints();
        groups.MapAlarmEndpoints();
        groups.MapIntegrationCredentialEndpoints();
        groups.MapMeshCentralManagementEndpoints();
        groups.MapNotificationEndpoints();
        return groups;
    }
}
