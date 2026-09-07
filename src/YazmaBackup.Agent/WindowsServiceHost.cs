using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public static class WindowsServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceAcceptPreshutdown = 0x00000100;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceControlPreshutdown = 0x0000000F;
    private const uint ErrorFailedServiceControllerConnect = 1063;

    private static CancellationTokenSource? _shutdown;
    private static Func<CancellationToken, Task>? _worker;
    private static IntPtr _statusHandle;
    private static ServiceMainFunction? _serviceMain;
    private static HandlerExFunction? _handler;
    private static uint _checkpoint;
    private static string _serviceName = "YazmaBackupAgent";

    public static void Run(string serviceName, Func<CancellationToken, Task> worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
        _serviceName = serviceName;
        _serviceMain = ServiceMain;
        _handler = Handler;

        var table = new[]
        {
            new ServiceTableEntry { ServiceName = serviceName, ServiceMain = _serviceMain },
            new ServiceTableEntry { ServiceName = null, ServiceMain = null }
        };

        if (!StartServiceCtrlDispatcher(table))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorFailedServiceControllerConnect)
                throw new InvalidOperationException("--service mode must be started by the Windows Service Control Manager.");
            throw new Win32Exception(error, "Could not connect YazmaBackup Agent to the Windows Service Control Manager.");
        }
    }

    private static void ServiceMain(uint argumentCount, IntPtr arguments)
    {
        _ = argumentCount;
        _ = arguments;
        _shutdown = new CancellationTokenSource();
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, _handler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            AgentLog.Error("service-register-failed", "Windows service control handler registration failed.", new { error = Marshal.GetLastWin32Error() });
            return;
        }

        SetStatus(ServiceStartPending, 0, waitHintMilliseconds: 15000);
        SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown | ServiceAcceptPreshutdown, 0);
        var exitCode = 0u;
        try
        {
            (_worker ?? throw new InvalidOperationException("Service worker was not initialized."))(_shutdown.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            exitCode = 1;
            AgentLog.Error("service-worker-crash", ex.Message, new { exceptionType = ex.GetType().FullName });
        }
        finally
        {
            SetStatus(ServiceStopped, 0, 0, exitCode);
            _shutdown.Dispose();
            _shutdown = null;
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;
        if (control is ServiceControlStop or ServiceControlShutdown or ServiceControlPreshutdown)
        {
            SetStatus(ServiceStopPending, 0, waitHintMilliseconds: 120000);
            _shutdown?.Cancel();
        }
        return 0;
    }

    private static void SetStatus(uint state, uint acceptedControls, uint waitHintMilliseconds, uint win32ExitCode = 0)
    {
        if (_statusHandle == IntPtr.Zero) return;
        var pending = state is ServiceStartPending or ServiceStopPending;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = 0,
            CheckPoint = pending ? ++_checkpoint : 0,
            WaitHint = waitHintMilliseconds
        };
        if (!SetServiceStatus(_statusHandle, ref status))
            AgentLog.Warning("service-status-failed", "Windows service status update failed.", new { error = Marshal.GetLastWin32Error(), state });
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainFunction(uint argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerExFunction(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceName;
        public ServiceMainFunction? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

#pragma warning disable SYSLIB1054 // SCM callback delegates require classic P/Invoke marshaling.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerExFunction handlerProc, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus serviceStatus);
#pragma warning restore SYSLIB1054
}
