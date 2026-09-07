namespace YazmaBackup.ControlPlane;

public sealed class ControlPlaneHaRuntime
{
    private int _draining;

    public ControlPlaneHaRuntime(string stateRoot, string role, string nodeId, string certificateThumbprint)
    {
        StateRoot = Path.GetFullPath(stateRoot);
        Role = role;
        NodeId = nodeId;
        CertificateThumbprint = certificateThumbprint;
        if (Role == "standby") _draining = 1;
    }

    public string StateRoot { get; }
    public string Role { get; }
    public string NodeId { get; }
    public string CertificateThumbprint { get; }
    public bool HaEnabled => Role is "active" or "standby" or "active-active";
    public bool CertificateProtected => !string.IsNullOrWhiteSpace(CertificateThumbprint);
    public bool Draining => Volatile.Read(ref _draining) == 1;
    public bool ReadyForTraffic => Role != "standby" && !Draining;
    public bool AcceptsMutations => ReadyForTraffic;

    public void SetDraining(bool draining) => Interlocked.Exchange(ref _draining, draining ? 1 : 0);
}

public sealed class ControlPlaneDrInventoryService(ControlPlaneHaRuntime ha)
{
    public object GetInventory()
    {
        var root = ha.StateRoot;
        var state = Path.Combine(root, "control-plane-state.json");
        var backup = state + ".bak";
        var dp = Path.Combine(root, "dataprotection-keys");
        var repositoryKeys = Path.Combine(root, "repository-key-vault");

        var files = new List<object>();
        AddFile(state, "control-plane-state", files);
        AddFile(backup, "control-plane-state-backup", files);
        AddFile(Path.Combine(root, "global-nas-profile.protected"), "global-nas-profile", files);
        AddFile(Path.Combine(root, "autonomous-orchestration.protected"), "autonomous-orchestration", files);
        AddFile(Path.Combine(root, "recovery-evidence-signing-key.protected"), "recovery-evidence-key", files);

        return new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            ha.NodeId,
            ha.Role,
            portable = ha.CertificateProtected,
            portabilityReason = ha.CertificateProtected
                ? "Shared Data Protection keys are certificate-protected. Restore node must have the same certificate private key."
                : "Data Protection keys use machine-local DPAPI. Cross-machine DR requires migration to certificate-protected Data Protection.",
            stateRoot = root,
            statePresent = File.Exists(state),
            stateBackupPresent = File.Exists(backup),
            dataProtectionKeyCount = Directory.Exists(dp) ? Directory.EnumerateFiles(dp, "*.xml", SearchOption.TopDirectoryOnly).Count() : 0,
            repositoryKeyFileCount = Directory.Exists(repositoryKeys) ? Directory.EnumerateFiles(repositoryKeys, "*", SearchOption.TopDirectoryOnly).Count() : 0,
            files
        };
    }

    private static void AddFile(string path, string category, List<object> files)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        files.Add(new
        {
            category,
            fileName = info.Name,
            length = info.Length,
            lastWriteTimeUtc = info.LastWriteTimeUtc
        });
    }
}
