using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public sealed partial class StateDocument
    {
        public string SchemaVersion { get; init; } = "11";
        public Dictionary<Guid, AgentRecord> Agents { get; init; } = [];
        public Dictionary<Guid, AgentCommand> Commands { get; init; } = [];
        public Dictionary<Guid, EnrollmentGrant> EnrollmentGrants { get; init; } = [];
        public Dictionary<Guid, BackupPolicyRecord> BackupPolicies { get; init; } = [];
        public Dictionary<Guid, ManagementUserRecord> ManagementUsers { get; init; } = [];
        public Dictionary<Guid, AuditEventRecord> AuditEvents { get; init; } = [];
        public Dictionary<Guid, ManagementApiTokenRecord> ManagementApiTokens { get; init; } = [];
        public Dictionary<Guid, BreakGlassRecoveryCodeRecord> BreakGlassRecoveryCodes { get; init; } = [];
        public Dictionary<Guid, ExternalIdentityRecord> ExternalIdentities { get; init; } = [];
        public Dictionary<Guid, AlarmRecord> Alarms { get; init; } = [];
        public Dictionary<string, ClusterLeaseRecord> ClusterLeases { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, RepositoryHealthRecord> RepositoryHealth { get; init; } = [];
        public Dictionary<Guid, RecoveryPlanRecord> RecoveryPlans { get; init; } = [];
        public Dictionary<Guid, BusinessServiceDependencyRecord> BusinessServiceDependencies { get; init; } = [];
        public Dictionary<Guid, DisasterRecoverySessionRecord> DisasterRecoverySessions { get; init; } = [];
        public Dictionary<Guid, RecoveryRunRecord> RecoveryRuns { get; init; } = [];
        public Dictionary<Guid, MeshCentralLinkRecord> MeshCentralLinks { get; init; } = [];
        public Dictionary<Guid, NotificationRouteRecord> NotificationRoutes { get; init; } = [];
        public Dictionary<Guid, NotificationDeliveryRecord> NotificationDeliveries { get; init; } = [];
        public Dictionary<Guid, RecoveryRunbookRecord> RecoveryRunbooks { get; init; } = [];
        public Dictionary<Guid, RecoveryRunbookRunRecord> RecoveryRunbookRuns { get; init; } = [];
        public Dictionary<Guid, IntegrationCredentialRecord> IntegrationCredentials { get; init; } = [];
        public Dictionary<Guid, MeshCentralSyncEventRecord> MeshCentralSyncEvents { get; init; } = [];
        public Dictionary<Guid, MeshCentralConnectorRecord> MeshCentralConnectors { get; init; } = [];
        public Dictionary<string, MeshCentralInventoryDeviceRecord> MeshCentralInventoryDevices { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, MeshCentralDeploymentRecord> MeshCentralDeployments { get; init; } = [];
    }
}
