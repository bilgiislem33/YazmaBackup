using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace YazmaBackup.ControlPlane;

public sealed record MeshBootstrapTicket(string Token, string NodeId, Guid DeploymentId, DateTimeOffset ExpiresAtUtc);
public sealed record AgentPackageTicket(string Token, Guid DeploymentId, DateTimeOffset ExpiresAtUtc);
public sealed record MeshEnrollmentCorrelation(string EnrollmentToken, string NodeId, Guid DeploymentId, DateTimeOffset ExpiresAtUtc);

public sealed class MeshCentralBootstrapTicketService
{
    private readonly ConcurrentDictionary<string, MeshBootstrapTicket> _bootstrap = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentPackageTicket> _packages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MeshEnrollmentCorrelation> _enrollments = new(StringComparer.Ordinal);

    public MeshBootstrapTicket IssueBootstrap(string nodeId, Guid deploymentId, TimeSpan ttl)
    {
        Prune();
        var token = "ybboot_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var ticket = new MeshBootstrapTicket(token, nodeId, deploymentId, DateTimeOffset.UtcNow.Add(ttl));
        _bootstrap[token] = ticket;
        return ticket;
    }

    public bool TryConsumeBootstrap(string token, out MeshBootstrapTicket? ticket)
    {
        ticket = null;
        if (!_bootstrap.TryRemove(token, out var found) || found.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
        ticket = found;
        return true;
    }

    public AgentPackageTicket IssuePackage(Guid deploymentId, TimeSpan ttl)
    {
        Prune();
        var token = "ybpkg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var ticket = new AgentPackageTicket(token, deploymentId, DateTimeOffset.UtcNow.Add(ttl));
        _packages[token] = ticket;
        return ticket;
    }

    public bool TryConsumePackage(string token, out AgentPackageTicket? ticket)
    {
        ticket = null;
        if (!_packages.TryRemove(token, out var found) || found.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
        ticket = found;
        return true;
    }

    public void TrackEnrollment(string enrollmentToken, string nodeId, Guid deploymentId, TimeSpan ttl)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(enrollmentToken)) throw new ArgumentException("Enrollment token is required.", nameof(enrollmentToken));
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("MeshCentral node id is required.", nameof(nodeId));
        _enrollments[enrollmentToken] = new MeshEnrollmentCorrelation(enrollmentToken, nodeId, deploymentId, DateTimeOffset.UtcNow.Add(ttl));
    }

    public bool TryConsumeEnrollment(string enrollmentToken, out MeshEnrollmentCorrelation? correlation)
    {
        correlation = null;
        if (!_enrollments.TryRemove(enrollmentToken, out var found) || found.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
        correlation = found;
        return true;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _bootstrap.Where(x => x.Value.ExpiresAtUtc <= now).ToArray()) _bootstrap.TryRemove(item.Key, out _);
        foreach (var item in _packages.Where(x => x.Value.ExpiresAtUtc <= now).ToArray()) _packages.TryRemove(item.Key, out _);
        foreach (var item in _enrollments.Where(x => x.Value.ExpiresAtUtc <= now).ToArray()) _enrollments.TryRemove(item.Key, out _);
    }
}
