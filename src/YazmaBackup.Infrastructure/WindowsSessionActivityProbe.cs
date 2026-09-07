using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace YazmaBackup.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionActivityProbe
{
    public static bool IsUserActive(TimeSpan idleThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(idleThreshold, TimeSpan.Zero);
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            return true;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(buffer, checked(index * size)));
                if (item.State is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSConnected))
                    continue;
                if (!WTSQuerySessionInformationW(IntPtr.Zero, item.SessionId, WTS_INFO_CLASS.WTSIdleTime, out var info, out var bytes))
                    return true;
                try
                {
                    if (bytes < sizeof(uint)) return true;
                    var idleSeconds = unchecked((uint)Marshal.ReadInt32(info));
                    if (TimeSpan.FromSeconds(idleSeconds) < idleThreshold) return true;
                }
                finally
                {
                    WTSFreeMemory(info);
                }
            }
            return false;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive = 0,
        WTSConnected = 1,
        WTSConnectQuery = 2,
        WTSShadow = 3,
        WTSDisconnected = 4,
        WTSIdle = 5,
        WTSListen = 6,
        WTSReset = 7,
        WTSDown = 8,
        WTSInit = 9
    }

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSClientBuildNumber = 9,
        WTSClientName = 10,
        WTSClientDirectory = 11,
        WTSClientProductId = 12,
        WTSClientHardwareId = 13,
        WTSClientAddress = 14,
        WTSClientDisplay = 15,
        WTSClientProtocolType = 16,
        WTSIdleTime = 17
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

#pragma warning disable SYSLIB1054
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
#pragma warning restore SYSLIB1054
}
