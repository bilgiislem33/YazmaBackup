using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace YazmaBackup.Agent;

[SupportedOSPlatform("windows")]
public sealed class MachineSecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;
    private readonly string _dataDescription;
    private readonly uint _protectFlags;

    public MachineSecretStore(string dataDescription = "YazmaBackup machine secret")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDescription);
        _dataDescription = dataDescription;
        _protectFlags = CryptProtectUiForbidden | CryptProtectLocalMachine;
    }

    public string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var protectedBytes = File.ReadAllBytes(path);
        var clear = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Write(string path, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        AgentPaths.EnsureDirectories();
        var clear = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = Protect(clear);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, protectedBytes);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private byte[] Protect(byte[] clear)
    {
        using var input = DataBlob.FromBytes(clear);
        if (!CryptProtectData(ref input.Value, _dataDescription, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                _protectFlags, out var output))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI protection failed.");
        try
        {
            return output.ToArray();
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private byte[] Unprotect(byte[] protectedBytes)
    {
        using var input = DataBlob.FromBytes(protectedBytes);
        IntPtr descriptionPtr = IntPtr.Zero;
        DATA_BLOB output = default;
        if (!CryptUnprotectData(ref input.Value, out descriptionPtr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out output))
        {
            var error = Marshal.GetLastWin32Error();
            if (descriptionPtr != IntPtr.Zero) LocalFree(descriptionPtr);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            throw new Win32Exception(error, "DPAPI unprotection failed.");
        }

        try
        {
            var actualDescription = descriptionPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(descriptionPtr);
            if (!string.Equals(actualDescription, _dataDescription, StringComparison.Ordinal))
                throw new CryptographicException("DPAPI data description mismatch.");
            return output.ToArray();
        }
        finally
        {
            if (descriptionPtr != IntPtr.Zero) LocalFree(descriptionPtr);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int Length;
        public IntPtr Data;

        public readonly byte[] ToArray()
        {
            if (Length <= 0 || Data == IntPtr.Zero) return [];
            var bytes = new byte[Length];
            Marshal.Copy(Data, bytes, 0, Length);
            return bytes;
        }
    }

    private sealed class DataBlob : IDisposable
    {
        public DATA_BLOB Value;

        private DataBlob(byte[] bytes)
        {
            Value = new DATA_BLOB { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, Value.Data, bytes.Length);
        }

        public static DataBlob FromBytes(byte[] bytes) => new(bytes);

        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero) return;
            Span<byte> zeros = Value.Length <= 4096 ? stackalloc byte[Value.Length] : new byte[Value.Length];
            zeros.Clear();
            Marshal.Copy(zeros.ToArray(), 0, Value.Data, Value.Length);
            Marshal.FreeHGlobal(Value.Data);
            Value.Data = IntPtr.Zero;
            Value.Length = 0;
        }
    }

#pragma warning disable SYSLIB1054 // DATA_BLOB/DPAPI interop intentionally uses classic marshaling.
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, out IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
#pragma warning restore SYSLIB1054
}
