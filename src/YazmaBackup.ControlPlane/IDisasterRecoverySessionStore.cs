using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public interface IDisasterRecoverySessionStore
{
    Task<IReadOnlyList<DisasterRecoverySessionRecord>> GetDisasterRecoverySessionsAsync(CancellationToken ct);
    Task<DisasterRecoverySessionRecord> CreateDisasterRecoverySessionAsync(string name, IReadOnlyList<DisasterRecoverySessionStepRecord> steps, CancellationToken ct);
    Task<DisasterRecoverySessionRecord> ApproveDisasterRecoveryStepAsync(Guid sessionId, int order, CancellationToken ct);
    Task<DisasterRecoverySessionRecord> VerifyDisasterRecoveryStepAsync(Guid sessionId, int order, string verificationNote, CancellationToken ct);
    Task<DisasterRecoverySessionRecord> CancelDisasterRecoverySessionAsync(Guid sessionId, CancellationToken ct);
}
