using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<IReadOnlyList<BusinessServiceDependencyRecord>> GetDependenciesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.BusinessServiceDependencies.Values.OrderBy(x => x.ServiceId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DependsOnServiceId, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<BusinessServiceDependencyRecord> UpsertDependencyAsync(
        string serviceId, string dependsOnServiceId, string dependencyType, bool required, CancellationToken ct)
    {
        serviceId=(serviceId??string.Empty).Trim();
        dependsOnServiceId=(dependsOnServiceId??string.Empty).Trim();
        dependencyType=(dependencyType??string.Empty).Trim().ToLowerInvariant();
        if(serviceId.Length is <1 or >120 || dependsOnServiceId.Length is <1 or >120)
            throw new ArgumentException("Business service identifiers must be 1-120 characters.");
        if(string.Equals(serviceId,dependsOnServiceId,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A business service cannot depend on itself.");
        if(dependencyType is not ("hard" or "soft"))
            throw new ArgumentException("Dependency type must be hard or soft.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next=CloneState();
            var existing=next.BusinessServiceDependencies.Values.FirstOrDefault(x=>
                string.Equals(x.ServiceId,serviceId,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DependsOnServiceId,dependsOnServiceId,StringComparison.OrdinalIgnoreCase));
            var record=existing is null
                ? new BusinessServiceDependencyRecord(Guid.NewGuid(),serviceId,dependsOnServiceId,dependencyType,required,DateTimeOffset.UtcNow)
                : existing with { DependencyType=dependencyType, Required=required };
            next.BusinessServiceDependencies[record.DependencyId]=record;
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteDependencyAsync(Guid dependencyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(!_state.BusinessServiceDependencies.ContainsKey(dependencyId)) return false;
            var next=CloneState();
            next.BusinessServiceDependencies.Remove(dependencyId);
            await CommitUnsafeAsync(next,ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }
}
