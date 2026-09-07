using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsUsnJournalChangeTracker(string checkpointDirectory) : IIncrementalChangeTracker
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint AllReasons = 0xFFFFFFFF;
    private const int ErrorHandleEof = 38;
    private const int OutputBufferSize = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _checkpointDirectory = Path.GetFullPath(checkpointDirectory);

    public async Task<IncrementalChangeSet> CaptureAsync(string sourceRoot, CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(normalizedRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot) || volumeRoot.StartsWith("\\\\", StringComparison.Ordinal))
            return FullScan("full-scan:usn-unsupported-path");

        Directory.CreateDirectory(_checkpointDirectory);
        var volume = volumeRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (volume.Length != 2 || volume[1] != ':')
            return FullScan("full-scan:usn-unsupported-volume");

        using var volumeHandle = CreateFileW(
            @"\\.\" + volume,
            GenericRead,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (volumeHandle.IsInvalid)
            return FullScan("full-scan:usn-volume-open-failed");

        if (!DeviceIoControlQuery(volumeHandle, FsctlQueryUsnJournal, IntPtr.Zero, 0, out var journal, (uint)Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
            return FullScan("full-scan:usn-query-failed");

        var checkpointPath = GetCheckpointPath(normalizedRoot);
        var checkpoint = await ReadCheckpointAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        var nextCheckpoint = new UsnCheckpoint(journal.UsnJournalId, journal.NextUsn, normalizedRoot, DateTimeOffset.UtcNow);
        if (checkpoint is null)
            return FullScanWithCommit("full-scan:usn-first-checkpoint", checkpointPath, nextCheckpoint);
        if (checkpoint.UsnJournalId != journal.UsnJournalId)
            return FullScanWithCommit("full-scan:usn-journal-recreated", checkpointPath, nextCheckpoint);
        if (checkpoint.NextUsn < journal.FirstUsn || checkpoint.NextUsn > journal.NextUsn)
            return FullScanWithCommit("full-scan:usn-checkpoint-invalid", checkpointPath, nextCheckpoint);
        if (checkpoint.NextUsn == journal.NextUsn)
            return new IncrementalChangeSet(true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "usn:no-changes", ct => WriteCheckpointAsync(checkpointPath, nextCheckpoint, ct));

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = false;
        var startUsn = checkpoint.NextUsn;
        var output = new byte[OutputBufferSize];

        while (startUsn < journal.NextUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = startUsn,
                ReasonMask = AllReasons,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = journal.UsnJournalId
            };

            if (!DeviceIoControlRead(volumeHandle, FsctlReadUsnJournal, ref request, (uint)Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(), output, (uint)output.Length, out var bytesReturned, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorHandleEof) break;
                return FullScanWithCommit("full-scan:usn-read-failed", checkpointPath, nextCheckpoint);
            }
            if (bytesReturned < sizeof(long)) break;

            var span = output.AsSpan(0, checked((int)bytesReturned));
            var returnedNextUsn = BinaryPrimitives.ReadInt64LittleEndian(span[..8]);
            if (returnedNextUsn <= startUsn) break;
            var offset = 8;
            while (offset + 60 <= span.Length)
            {
                var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                if (recordLength < 60 || offset + recordLength > span.Length)
                {
                    unresolved = true;
                    break;
                }
                var major = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 4, 2));
                if (major != 2)
                {
                    unresolved = true;
                    offset += checked((int)recordLength);
                    continue;
                }

                var fileReference = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 8, 8));
                var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 16, 8));
                var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 56, 2));
                var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 58, 2));
                if (fileNameOffset + fileNameLength > recordLength || fileNameLength % 2 != 0)
                {
                    unresolved = true;
                    offset += checked((int)recordLength);
                    continue;
                }
                var fileName = Encoding.Unicode.GetString(span.Slice(offset + fileNameOffset, fileNameLength));
                var fullPath = ResolveFileReference(volumeHandle, fileReference);
                if (fullPath is null)
                {
                    var parentPath = ResolveFileReference(volumeHandle, parentReference);
                    if (parentPath is not null) fullPath = Path.Combine(parentPath, fileName);
                }

                if (fullPath is null)
                {
                    unresolved = true;
                }
                else if (IsUnderRoot(normalizedRoot, fullPath))
                {
                    var relative = Path.GetRelativePath(normalizedRoot, fullPath);
                    if (!string.Equals(relative, ".", StringComparison.Ordinal))
                        changed.Add(NormalizeRelative(relative));
                }
                offset += checked((int)recordLength);
            }

            startUsn = returnedNextUsn;
        }

        if (unresolved)
            return FullScanWithCommit("full-scan:usn-unresolved-record", checkpointPath, nextCheckpoint);

        return new IncrementalChangeSet(
            true,
            changed,
            changed.Count == 0 ? "usn:no-relevant-changes" : "usn:changed-paths",
            ct => WriteCheckpointAsync(checkpointPath, nextCheckpoint, ct));
    }

    private static string? ResolveFileReference(SafeFileHandle volumeHandle, ulong fileReference)
    {
        var descriptor = new FILE_ID_DESCRIPTOR
        {
            Size = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = FILE_ID_TYPE.FileIdType,
            FileId = unchecked((long)fileReference)
        };
        using var handle = OpenFileById(volumeHandle, ref descriptor, 0, FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, FileFlagBackupSemantics);
        if (handle.IsInvalid) return null;
        var capacity = 1024;
        while (capacity <= 32768)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length)
                return NormalizeFinalPath(new string(buffer, 0, (int)length));
            capacity = checked((int)length + 1);
        }
        return null;
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        if (path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            return path[localPrefix.Length..];
        return path;
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, normalizedCandidate, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private string GetCheckpointPath(string sourceRoot)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceRoot.ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(_checkpointDirectory, hash + ".json");
    }

    private static async Task<UsnCheckpoint?> ReadCheckpointAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<UsnCheckpoint>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteCheckpointAsync(string path, UsnCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, checkpoint, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static IncrementalChangeSet FullScan(string mode) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), mode, _ => Task.CompletedTask);

    private static IncrementalChangeSet FullScanWithCommit(string mode, string checkpointPath, UsnCheckpoint checkpoint) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), mode, ct => WriteCheckpointAsync(checkpointPath, checkpoint, ct));

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    private enum FILE_ID_TYPE : uint
    {
        FileIdType = 0
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FILE_ID_DESCRIPTOR
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public FILE_ID_TYPE Type;
        [FieldOffset(8)] public long FileId;
        [FieldOffset(8)] public Guid ObjectId;
    }

    private sealed record UsnCheckpoint(ulong UsnJournalId, long NextUsn, string SourceRoot, DateTimeOffset CapturedAtUtc);

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlQuery(SafeFileHandle device, uint controlCode, IntPtr inputBuffer, uint inputSize, out USN_JOURNAL_DATA_V0 outputBuffer, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlRead(SafeFileHandle device, uint controlCode, ref READ_USN_JOURNAL_DATA_V0 inputBuffer, uint inputSize, byte[] outputBuffer, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR fileId, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint flagsAndAttributes);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
#pragma warning restore SYSLIB1054
}
