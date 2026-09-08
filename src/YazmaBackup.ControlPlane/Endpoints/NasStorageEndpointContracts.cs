namespace YazmaBackup.ControlPlane.Endpoints;

internal static class NasStorageEndpointContracts
{
    internal const string GlobalProfile = "/nas/global-profile";
    internal const string SaveAndTestCredential = "/agents/{agentId:guid}/nas-credential/save-and-test";
    internal const string ProvisionCredential = "/agents/{agentId:guid}/nas-credential";
    internal const string TestAccess = "/agents/{agentId:guid}/nas-access-test";

    internal static IReadOnlyList<string> ReadRoutes { get; } = [GlobalProfile];
    internal static IReadOnlyList<string> OperateRoutes { get; } = [TestAccess];
    internal static IReadOnlyList<string> SecurityRoutes { get; } =
    [
        GlobalProfile,
        SaveAndTestCredential,
        ProvisionCredential
    ];
}
