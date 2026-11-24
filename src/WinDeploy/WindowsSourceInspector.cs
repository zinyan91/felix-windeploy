using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;
using Microsoft.Win32.SafeHandles;

namespace Felix.WindowSetup;

public sealed class WindowsSourceInspector : IImageInspector
{
    public async Task<WindowsImageInspection> InspectAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(imagePath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return await InspectWimFileAsync(imagePath, cancellationToken);
        }

        using var mount = await IsoMountLease.AttachAsync(imagePath, cancellationToken);
        var installImage = new[]
            {
                Path.Combine(mount.RootPath, "sources", "install.wim"),
                Path.Combine(mount.RootPath, "sources", "install.esd"),
                Path.Combine(mount.RootPath, "sources", "install.swm")
            }
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The ISO does not contain sources\\install.wim, install.esd, or install.swm.");
        var inspection = await InspectWimFileAsync(installImage, cancellationToken);
        return new WindowsImageInspection(imagePath, inspection.Editions);
    }

    private static Task<WindowsImageInspection> InspectWimFileAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        string? wimlibPath = null;
        try
        {
            wimlibPath = WindowSetupEngineLocator.Find();
        }
        catch
        {
        }

        return !string.IsNullOrEmpty(wimlibPath)
            ? WimlibWindowsImageInspector.InspectAsync(wimlibPath, imagePath, cancellationToken)
            : DismWindowsImageInspector.InspectAsync(imagePath, cancellationToken);
    }
}

internal sealed class IsoMountLease : IDisposable
{
    private const uint OpenVirtualDiskAccessAttachReadOnly = 0x00010000;
    private const uint OpenVirtualDiskAccessGetInfo = 0x00080000;
    private const uint AttachVirtualDiskFlagReadOnly = 0x00000001;
    private static readonly Guid MicrosoftVendor =
        new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    private readonly SafeFileHandle handle;
    private readonly bool detachOnDispose;
    private bool attached;

    public string RootPath { get; }

    private IsoMountLease(SafeFileHandle handle, string rootPath, bool detachOnDispose)
    {
        this.handle = handle;
        RootPath = rootPath;
        this.detachOnDispose = detachOnDispose;
        attached = detachOnDispose;
    }

    public static async Task<IsoMountLease> AttachAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected ISO does not exist.", path);
        }

        var storage = new VirtualStorageType
        {
            DeviceId = 1,
            VendorId = MicrosoftVendor
        };
        var open = new OpenVirtualDiskParameters
        {
            Version = 1,
            ReadWriteDepth = 1
        };
        var code = OpenVirtualDisk(
            ref storage,
            Path.GetFullPath(path),
            OpenVirtualDiskAccessAttachReadOnly | OpenVirtualDiskAccessGetInfo,
            0,
            ref open,
            out var handle);
        if (code != 0)
        {
            handle?.Dispose();
            throw new Win32Exception(checked((int)code), "Windows could not open the ISO.");
        }

        var attachedNow = false;
        try
        {
            var existingPath = new StringBuilder(256);
            uint existingSize = 512;
            if (GetVirtualDiskPhysicalPath(handle, ref existingSize, existingPath) == 0)
            {
                var existingDevice = existingPath.ToString().Replace(@"\\.\", @"\Device\", StringComparison.OrdinalIgnoreCase);
                var existingRoot = FindDriveRoot(existingDevice);
                if (existingRoot is not null)
                {
                    return new IsoMountLease(handle, existingRoot, detachOnDispose: false);
                }
            }

            var attach = new AttachVirtualDiskParameters { Version = 1 };
            code = AttachVirtualDisk(
                handle,
                0,
                AttachVirtualDiskFlagReadOnly,
                0,
                ref attach,
                0);
            if (code != 0)
            {
                throw new Win32Exception(checked((int)code), "Windows could not attach the ISO.");
            }
            attachedNow = true;

            var physicalPath = new StringBuilder(256);
            uint size = 512;
            code = GetVirtualDiskPhysicalPath(handle, ref size, physicalPath);
            if (code != 0)
            {
                throw new Win32Exception(checked((int)code), "Windows could not identify the attached ISO.");
            }

            var device = physicalPath.ToString().Replace(@"\\.\", @"\Device\", StringComparison.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = FindDriveRoot(device);
                if (root is not null)
                {
                    return new IsoMountLease(handle, root, detachOnDispose: true);
                }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("The attached ISO did not receive a drive letter.");
        }
        catch
        {
            try
            {
                if (attachedNow)
                {
                    DetachVirtualDisk(handle, 0, 0);
                }
            }
            finally
            {
                handle.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (handle.IsClosed)
        {
            return;
        }
        uint code = 0;
        try
        {
            if (detachOnDispose && attached)
            {
                code = DetachVirtualDisk(handle, 0, 0);
            }
            attached = false;
        }
        finally
        {
            handle.Dispose();
        }
        if (detachOnDispose && code != 0)
        {
            throw new Win32Exception(checked((int)code), "Windows could not detach the ISO.");
        }
    }

    private static string? FindDriveRoot(string device)
    {
        foreach (var letter in Enumerable.Range('D', 23).Select(value => (char)value))
        {
            var name = $"{letter}:";
            var target = new StringBuilder(1024);
            if (QueryDosDevice(name, target, target.Capacity) == 0)
            {
                continue;
            }
            if (target.ToString().Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                return name + "\\";
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenVirtualDiskParameters
    {
        public uint Version;
        public uint ReadWriteDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttachVirtualDiskParameters
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint accessMask,
        uint flags,
        ref OpenVirtualDiskParameters parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint AttachVirtualDisk(
        SafeFileHandle handle,
        nint securityDescriptor,
        uint flags,
        uint providerFlags,
        ref AttachVirtualDiskParameters parameters,
        nint overlapped);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle handle,
        ref uint size,
        StringBuilder path);

    [DllImport("virtdisk.dll")]
    private static extern uint DetachVirtualDisk(
        SafeFileHandle handle,
        uint flags,
        uint providerFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(
        string deviceName,
        StringBuilder targetPath,
        int maximumLength);
}
