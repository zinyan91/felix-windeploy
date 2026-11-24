using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Felix.Partition.Services.Native;

internal static partial class Win32
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetOEMCP();

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    public static unsafe string FinalVolumePath(SafeFileHandle file)
    {
        var buffer = new char[32768];
        fixed (char* path = buffer)
        {
            var length = GetFinalPathNameByHandle(file, path, (uint)buffer.Length, 1);
            if (length == 0 || length >= buffer.Length) throw new InvalidOperationException("Partition_Error_JournalLocation");
            return new string(buffer, 0, (int)length);
        }
    }

    public const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskGetDiskAttributes = 0x000700F0;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlMountdevQueryUniqueId = 0x004D0000;

    public static byte[] MountUniqueId(string volume)
    {
        using var handle = OpenDevice(volume.TrimEnd('\\'));
        var bytes = handle == null ? null : Query(handle, IoctlMountdevQueryUniqueId, initialSize: 512);
        if (bytes is not { Length: >= 2 }) throw new InvalidOperationException("Partition_Error_MigrationMounts");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (length == 0 || length > bytes.Length - 2) throw new InvalidOperationException("Partition_Error_MigrationMounts");
        return bytes.AsSpan(2, length).ToArray();
    }

    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize,
        void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    private static unsafe partial nint FindFirstVolume(char* name, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextVolume(nint find, char* name, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeClose(nint find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathNamesForVolumeName(string volume, char* names, int length, out int needed);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(string root, char* label, int labelLength, out uint serial,
        out uint maxComponent, out uint flags, char* fileSystem, int fileSystemLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformationByHandle(SafeFileHandle volume, char* label, int labelLength,
        out uint serial, out uint maxComponent, out uint flags, char* fileSystem, int fileSystemLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* name, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetVolumeLabelW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVolumeLabelNative(string root, string label);

    [LibraryImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetVolumeMountPointNative(string mountPoint, string volume);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteVolumeMountPointNative(string mountPoint);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetLogicalDrives();

    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlUnlockVolume = 0x0009001C;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint IoctlDiskSetDriveLayoutEx = 0x0007C054;
    public const uint IoctlDiskUpdateProperties = 0x00070140;

    public static unsafe void ControlIn(SafeFileHandle device, uint code, ReadOnlySpan<byte> input)
    {
        fixed (byte* i = input)
            if (!DeviceIoControl(device, code, i, input.Length, null, 0, out _, 0))
                throw new InvalidOperationException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }
    private const uint GenericWrite = 0x40000000;
    // Write-through only: unbuffered I/O would also demand sector-aligned memory, which managed arrays do not guarantee.
    private const uint FileFlagWriteThrough = 0x80000000;

    public static SafeFileHandle OpenForWrite(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting,
            FileFlagWriteThrough, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new InvalidOperationException(new Win32Exception(error).Message);
    }

    public static unsafe bool Control(SafeFileHandle device, uint code) =>
        DeviceIoControl(device, code, null, 0, null, 0, out _, 0);

    /// <summary>Opens a device for IOCTLs. Zero access is enough for queries and does not require administrator rights.</summary>
    public static SafeFileHandle? OpenDevice(string path, bool read = false)
    {
        var handle = CreateFile(path, read ? GenericRead : 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Runs a buffered IOCTL, growing the output buffer while the driver reports it is too small.</summary>
    public static unsafe byte[]? Query(SafeFileHandle device, uint code, ReadOnlySpan<byte> input = default, int initialSize = 4096)
    {
        for (var size = initialSize; size <= 1 << 20; size *= 4)
        {
            var output = new byte[size];
            fixed (byte* i = input)
            fixed (byte* o = output)
            {
                if (DeviceIoControl(device, code, i, input.Length, o, output.Length, out var returned, 0))
                    return output.AsSpan(0, returned).ToArray();
            }
            var error = Marshal.GetLastPInvokeError();
            if (error is not (122 or 234)) return null; // ERROR_INSUFFICIENT_BUFFER, ERROR_MORE_DATA
        }
        return null;
    }

    public static unsafe List<string> Volumes()
    {
        var result = new List<string>();
        var buffer = stackalloc char[260];
        var find = FindFirstVolume(buffer, 260);
        if (find == -1) return result;
        try
        {
            do result.Add(new string(buffer));
            while (FindNextVolume(find, buffer, 260));
        }
        finally { FindVolumeClose(find); }
        return result;
    }

    public static unsafe List<string> MountPaths(string volume)
    {
        var buffer = new char[1024];
        while (true)
        {
            fixed (char* b = buffer)
            {
                if (GetVolumePathNamesForVolumeName(volume, b, buffer.Length, out var needed))
                    return new string(buffer, 0, Math.Max(0, needed)).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (Marshal.GetLastPInvokeError() != 234 || needed <= buffer.Length) return [];
                buffer = new char[needed];
            }
        }
    }

    public static unsafe (string Label, string FileSystem, uint Serial)? VolumeInformation(string root)
    {
        var label = stackalloc char[261];
        var fileSystem = stackalloc char[261];
        if (!GetVolumeInformation(root, label, 261, out var serial, out _, out _, fileSystem, 261)) return null;
        return (new string(label), new string(fileSystem), serial);
    }

    public static unsafe (string Label, string FileSystem, uint Serial)? VolumeInformation(SafeFileHandle volume)
    {
        var label = stackalloc char[261];
        var fileSystem = stackalloc char[261];
        if (!GetVolumeInformationByHandle(volume, label, 261, out var serial, out _, out _, fileSystem, 261)) return null;
        return (new string(label), new string(fileSystem), serial);
    }

    public static long? FreeBytes(string root) =>
        GetDiskFreeSpaceEx(root, out _, out _, out var free) ? (long)free : null;
    public static long? AvailableBytes(string root) =>
        GetDiskFreeSpaceEx(root, out var available, out _, out _) && available <= long.MaxValue ? (long)available : null;

    public static unsafe string? VolumeNameOf(string mountPoint)
    {
        var buffer = stackalloc char[260];
        return GetVolumeNameForVolumeMountPoint(mountPoint, buffer, 260) ? new string(buffer) : null;
    }

    public static void SetVolumeLabel(string root, string label) => Check(SetVolumeLabelNative(root, label));
    public static void SetVolumeMountPoint(string mountPoint, string volume) => Check(SetVolumeMountPointNative(mountPoint, volume));
    public static void DeleteVolumeMountPoint(string mountPoint) => Check(DeleteVolumeMountPointNative(mountPoint));

    private static void Check(bool ok)
    {
        if (!ok) throw new InvalidOperationException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    public static string Utf16Z(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    public static string AsciiZ(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset <= 0 || offset >= bytes.Length) return "";
        var slice = bytes[offset..];
        var end = slice.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? slice : slice[..end]).Trim();
    }
}
