using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IResilienceStore
{
    Task<IReadOnlyList<RepositoryHealthRecord>> GetRepositoryHealthAsync(int limit, CancellationToken ct);
    Task<RecoveryPlanRecord> CreateRecoveryPlanAsync(string name, IReadOnlyList<Guid> policyIds, int maxParallelAgents, int rtoTargetMinutes, int intervalDays, bool enabled, CancellationToken ct);
    Task<IReadOnlyList<RecoveryPlanRecord>> GetRecoveryPlansAsync(CancellationToken ct);
    Task<IReadOnlyList<RecoveryRunRecord>> GetRecoveryRunsAsync(int limit, CancellationToken ct);
    Task<int> EnqueueDueResilienceAsync(DateTimeOffset nowUtc, CancellationToken ct);
    Task<MeshCentralLinkRecord> UpsertMeshCentralLinkAsync(Guid agentId, string baseUri, string nodeId, CancellationToken ct);
    Task<IReadOnlyList<MeshCentralLinkRecord>> GetMeshCentralLinksAsync(CancellationToken ct);
    Task<NotificationRouteRecord> CreateNotificationRouteAsync(NotificationRouteRecord route, CancellationToken ct);
    Task<IReadOnlyList<NotificationRouteRecord>> GetNotificationRoutesAsync(CancellationToken ct);
    Task<bool> DeleteNotificationRouteAsync(Guid routeId, CancellationToken ct);
    Task<IReadOnlyList<NotificationDeliveryRecord>> PrepareNotificationDeliveriesAsync(DateTimeOffset nowUtc, CancellationToken ct);
    Task CompleteNotificationDeliveryAsync(Guid deliveryId, bool succeeded, string? errorMessage, DateTimeOffset nowUtc, CancellationToken ct);
    Task<IReadOnlyList<NotificationDeliveryRecord>> GetNotificationDeliveriesAsync(int limit, CancellationToken ct);
}
