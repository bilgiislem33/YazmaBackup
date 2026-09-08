namespace YazmaBackup.ControlPlane.Endpoints;

/// <summary>
/// Central route contract for the agent-facing control-plane API. Keeping these paths in one
/// place lets endpoint modules and contract tests evolve without duplicating security-sensitive
/// route literals throughout the composition root.
/// </summary>
internal static class AgentEndpointContracts
{
    internal const string Register = "/api/v1/agents/register";
    internal const string Heartbeat = "/api/v1/agent/heartbeat";
    internal const string TransferTelemetry = "/api/v1/agent/transfer-telemetry";
    internal const string NextCommand = "/api/v1/agent/commands/next";
    internal const string RenewCommandLease = "/api/v1/agent/commands/{commandId:guid}/lease";
    internal const string CompleteCommand = "/api/v1/agent/commands/{commandId:guid}/result";

    internal static IReadOnlyList<string> EnrollmentRoutes { get; } = [Register];
    internal static IReadOnlyList<string> RuntimeRoutes { get; } = [Heartbeat, TransferTelemetry];
    internal static IReadOnlyList<string> CommandRoutes { get; } = [NextCommand, RenewCommandLease, CompleteCommand];
}
