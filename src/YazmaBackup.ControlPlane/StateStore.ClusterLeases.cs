using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    public async Task<ClusterLeaseRecord?> TryAcquireOrRenewClusterLeaseAsync(string leaseName, string ownerId, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken ct)
    {
        leaseName = NormalizeLeasePart(leaseName, 128, nameof(leaseName));
        ownerId = NormalizeLeasePart(ownerId, 128, nameof(ownerId));
        if (duration < TimeSpan.FromSeconds(10) || duration > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(duration));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.ClusterLeases.TryGetValue(leaseName, out var current);
            if (current is not null && current.ExpiresAtUtc > nowUtc && !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)) return null;
            var lease = current is not null && string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal)
                ? current with { ExpiresAtUtc = nowUtc.Add(duration) }
                : new ClusterLeaseRecord(leaseName, ownerId, Guid.NewGuid(), (current?.Epoch ?? 0) + 1, nowUtc, nowUtc.Add(duration));
            var nextState = CloneState();
            nextState.ClusterLeases[leaseName] = lease;
            await CommitUnsafeAsync(nextState, ct).ConfigureAwait(false);
            return lease;
        }
        finally { _gate.Release(); }
    }

    public async Task<ClusterLeaseRecord?> GetClusterLeaseAsync(string leaseName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _state.ClusterLeases.TryGetValue(leaseName, out var lease) ? lease : null; }
        finally { _gate.Release(); }
    }

    private static string NormalizeLeasePart(string value, int maxLength, string parameterName)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 || normalized.Length > maxLength || !normalized.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) throw new ArgumentException("Cluster lease value is invalid.", parameterName);
        return normalized;
    }
}
