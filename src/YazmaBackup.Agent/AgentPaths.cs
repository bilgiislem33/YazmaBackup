namespace YazmaBackup.Agent;

public static class AgentPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YazmaBackup");
    public static string ConfigFile => Path.Combine(Root, "agent-config.json");
    public static string IdentityFile => Path.Combine(Root, "agent.json");
    public static string AccessTokenFile => Path.Combine(Root, "agent-access-token.dpapi");
    public static string EnrollmentTokenFile => Path.Combine(Root, "bootstrap-enrollment-token.dpapi");
    public static string LogDirectory => Path.Combine(Root, "logs");
    public static string CommandResultDirectory => Path.Combine(Root, "command-results");
    public static string UpdateDirectory => Path.Combine(Root, "updates");
    public static string DefaultUpdatePublicKeyFile => Path.Combine(Root, "update-public-key.pem");
    public static string RepositoryKeyDirectory => Path.Combine(Root, "repository-keys");
    public static string NasCredentialDirectory => Path.Combine(Root, "nas-credentials");
    public static string ChangeTrackingDirectory => Path.Combine(Root, "change-tracking");
    public static string KeyExchangePrivateKeyFile => Path.Combine(Root, "key-exchange-private-key.dpapi");
    public static string ProtectionLockFile => Path.Combine(Root, "protection-lock.dpapi");
    public static string RestoreDrillDirectory => Path.Combine(Root, "restore-drills");
    public static string RepositoryCircuitStateFile => Path.Combine(Root, "repository-circuits.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CommandResultDirectory);
        Directory.CreateDirectory(UpdateDirectory);
        Directory.CreateDirectory(RepositoryKeyDirectory);
        Directory.CreateDirectory(NasCredentialDirectory);
        Directory.CreateDirectory(ChangeTrackingDirectory);
        Directory.CreateDirectory(RestoreDrillDirectory);
    }
}
