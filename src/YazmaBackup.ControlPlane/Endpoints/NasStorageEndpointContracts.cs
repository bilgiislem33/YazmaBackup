namespace YazmaBackup.ControlPlane.Endpoints;

internal static class NasStorageEndpointContracts
{
    internal const string GlobalProfile = "/nas/global-profile";

    internal static IReadOnlyList<string> ReadRoutes { get; } = [GlobalProfile];
    internal static IReadOnlyList<string> SecurityRoutes { get; } = [GlobalProfile];
}
