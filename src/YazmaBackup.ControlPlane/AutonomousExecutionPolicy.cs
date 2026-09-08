namespace YazmaBackup.ControlPlane;

public sealed record AutonomousExecutionPolicy(bool Enabled)
{
    public const string EnvironmentVariable = "YAZMABACKUP_ENABLE_AUTONOMOUS_EXECUTION";
}
