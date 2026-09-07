using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IBusinessContinuityStore
{
    Task<IReadOnlyList<BusinessServiceDependencyRecord>> GetDependenciesAsync(CancellationToken ct);
    Task<BusinessServiceDependencyRecord> UpsertDependencyAsync(
        string serviceId, string dependsOnServiceId, string dependencyType, bool required, CancellationToken ct);
    Task<bool> DeleteDependencyAsync(Guid dependencyId, CancellationToken ct);
}
