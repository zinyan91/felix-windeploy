using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Felix.Partition.Services.Native;
using Microsoft.Win32.SafeHandles;

internal sealed class DisposableVhdx : IDisposable
{
    private readonly SafeFileHandle handle;
    private bool attached;

    public string Path { get; }
    public long Size { get; }
    public int DiskNumber { get; }

    private DisposableVhdx(string path, long size, int sectorSize)
    {
        Path = System.IO.Path.GetFullPath(path);
        Size = size;
        var storage = new VirtualStorageType
        {
            DeviceId = 3,
            VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
        };
        var parameters = new CreateParameters
        {
            Version = 2,
            Details = new CreateDetails
            {
                UniqueId = Guid.NewGuid(),
                MaximumSize = checked((ulong)size),
                BlockSize = 2 * 1024 * 1024,
                SectorSize = checked((uint)sectorSize),
                PhysicalSectorSize = checked((uint)sectorSize)
            }
        };
        var code = CreateVirtualDisk(
            ref storage,
            Path,
            0,
            0,
            0,
            0,
            ref parameters,
            0,
            out handle);
        if (code != 0)
        {
            handle?.Dispose();
            throw Error(code);
        }
        try
        {
            var attach = new AttachParameters { Version = 1 };
            code = AttachVirtualDisk(handle, 0, 2, 0, ref attach, 0);
            if (code != 0)
            {
                throw Error(code);
            }
            attached = true;
            var physicalPath = new StringBuilder(256);
            uint bytes = 512;
            code = GetVirtualDiskPhysicalPath(handle, ref bytes, physicalPath);
            if (code != 0)
            {
                throw Error(code);
            }
            var text = physicalPath.ToString();
            if (!text.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(text[@"\\.\PhysicalDrive".Length..], out var number))
            {
                throw Error(0);
            }
            DiskNumber = number;
            var probe = NativeStorageReader.ProbeDisk(DiskNumber)
                ?? throw Error(0);
            if (probe.Size != Size)
            {
                throw Error(0);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static DisposableVhdx Create(string path, long size, int sectorSize) =>
        new(path, size, sectorSize);

    public void Dispose()
    {
        if (handle.IsClosed)
        {
            return;
        }
        uint code = 0;
        try
        {
            if (attached)
            {
                code = DetachVirtualDisk(handle, 0, 0);
            }
            attached = false;
        }
        finally
        {
            handle.Dispose();
        }
        if (code != 0)
        {
            throw Error(code);
        }
    }

    private static InvalidOperationException Error(uint code) =>
        new(
            "Disposable VHDX could not be created or attached.",
            code == 0 ? null : new Win32Exception(checked((int)code)));

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateDetails
    {
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSize;
        public uint SectorSize;
        public uint PhysicalSectorSize;
        public nint ParentPath;
        public nint SourcePath;
        public uint OpenFlags;
        public VirtualStorageType ParentStorage;
        public VirtualStorageType SourceStorage;
        public Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateParameters
    {
        public uint Version;
        public CreateDetails Details;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachParameters
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint CreateVirtualDisk(
        ref VirtualStorageType storage,
        string path,
        uint access,
        nint security,
        uint flags,
        uint providerFlags,
        ref CreateParameters parameters,
        nint overlapped,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(
        SafeFileHandle disk,
        nint security,
        uint flags,
        uint providerFlags,
        ref AttachParameters parameters,
        nint overlapped);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle disk,
        ref uint size,
        StringBuilder path);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(
        SafeFileHandle disk,
        uint flags,
        uint providerFlags);
}
