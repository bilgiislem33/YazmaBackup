namespace YazmaBackup.ControlPlane;

public sealed record UpsertBusinessServiceDependencyRequest(
    string ServiceId,
    string DependsOnServiceId,
    string DependencyType,
    bool Required);

public sealed record CreateDrSessionRequest(string Name);

public sealed record VerifyDrStepRequest(string VerificationNote);