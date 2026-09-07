namespace YazmaBackup.ControlPlane;

internal static class AutonomousMutationAllowlist
{
    internal static readonly string[] Values =
    [
        "reset-repository-circuit",
        "cleanup-stale-restore-temp"
    ];
}
