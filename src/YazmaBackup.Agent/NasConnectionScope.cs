using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using YazmaBackup.Contracts;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed record NasConnectionAttempt(bool Succeeded, string ShareRoot, int ErrorCode, string Message, NasConnectionScope? Scope);

[SupportedOSPlatform("windows")]
public sealed class NasConnectionScope : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int NoError = 0;
    private const int ErrorAlreadyAssigned = 85;
    private readonly string? _shareRoot;
    private readonly bool _connectedByUs;

    private NasConnectionScope(string? shareRoot, bool connectedByUs)
    {
        _shareRoot = shareRoot;
        _connectedByUs = connectedByUs;
    }

    public static NasConnectionScope ConnectIfConfigured(string repositoryRoot, NasCredentialSecret? credential)
    {
        if (credential is null || !TryGetShareRoot(repositoryRoot, out var shareRoot))
            return new NasConnectionScope(null, false);

        var result = ConnectCore(shareRoot, credential);
        if (result.ErrorCode is NoError or ErrorAlreadyAssigned)
            return new NasConnectionScope(shareRoot, result.ErrorCode == NoError);

        throw new Win32Exception(result.ErrorCode, $"SMB connection to '{shareRoot}' failed. WindowsError={result.ErrorCode}; {result.Message}");
    }

    public static NasConnectionAttempt ConnectForTest(string repositoryRoot, NasCredentialSecret credential)
    {
        if (!TryGetShareRoot(repositoryRoot, out var shareRoot))
            return new NasConnectionAttempt(false, string.Empty, 87, "UNC yolu \\\\sunucu\\paylasim biçiminde değil.", null);

        var result = ConnectCore(shareRoot, credential);
        if (result.ErrorCode is NoError or ErrorAlreadyAssigned)
            return new NasConnectionAttempt(true, shareRoot, result.ErrorCode, result.Message, new NasConnectionScope(shareRoot, result.ErrorCode == NoError));

        return new NasConnectionAttempt(false, shareRoot, result.ErrorCode, result.Message, null);
    }

    private static NasConnectionAttempt ConnectCore(string shareRoot, NasCredentialSecret credential)
    {
        var resource = new NETRESOURCE { dwType = ResourceTypeDisk, lpRemoteName = shareRoot };
        var error = WNetAddConnection2(ref resource, credential.Password, credential.Username, 0);
        var message = error == NoError
            ? "SMB oturumu oluşturuldu."
            : error == ErrorAlreadyAssigned
                ? "SMB oturumu zaten mevcut."
                : new Win32Exception(error).Message;
        return new NasConnectionAttempt(error is NoError or ErrorAlreadyAssigned, shareRoot, error, message, null);
    }

    public static bool TryGetShareRoot(string path, out string shareRoot)
    {
        shareRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        shareRoot = $@"\\{parts[0]}\{parts[1]}";
        return true;
    }

    public void Dispose()
    {
        if (_connectedByUs && !string.IsNullOrWhiteSpace(_shareRoot))
            _ = WNetCancelConnection2(_shareRoot, 0, true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

#pragma warning disable SYSLIB1054
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string? lpPassword, string? lpUserName, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool fForce);
#pragma warning restore SYSLIB1054
}
