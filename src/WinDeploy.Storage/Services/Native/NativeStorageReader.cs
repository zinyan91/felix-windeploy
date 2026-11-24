using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Felix.Partition.Services.Native;

/// <summary>Reads disks and volumes through IOCTLs and volume APIs only, so the same code runs in Windows and WinPE.</summary>
public static class NativeStorageReader
{
    private const int MaxDisks = 128;
    private static readonly byte[] BitLockerSignature = "-FVE-FS-"u8.ToArray();

    public static StorageSnapshot Read()
    {
        var disks = new List<DiskProbe>();
        for (var n = 0; n < MaxDisks; n++)
            if (ProbeDisk(n) is DiskProbe disk) disks.Add(disk);
        var volumes = ProbeVolumes(out var dynamicDisks);
        return StorageMapping.Build(disks, volumes, OccupiedLetters(), Encryption, dynamicDisks);
    }

    public static DiskProbe? ProbeDisk(int number, bool readLayout = true)
    {
        using var device = Win32.OpenDevice($@"\\.\PhysicalDrive{number}");
        if (device == null) return null;
        return ProbeDisk(device, number, readLayout);
    }

    internal static DiskProbe? ProbeDisk(SafeFileHandle device, int number, bool readLayout = true)
    {
        // No geometry means no medium (empty card reader); such a device has nothing to show or edit.
        var geometry = Win32.Query(device, Win32.IoctlDiskGetDriveGeometryEx, initialSize: 256);
        if (geometry == null || geometry.Length < 32) return null;
        var size = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(24));
        if (size <= 0) return null;
        var sectorSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(20));
        var layoutBytes = readLayout ? Win32.Query(device, Win32.IoctlDiskGetDriveLayoutEx, initialSize: 48 + 144 * 16) : null;
        var layout = layoutBytes == null ? null : DriveLayoutParser.Parse(layoutBytes);
        var query = new byte[12];
        var descriptor = Win32.Query(device, Win32.IoctlStorageQueryProperty, query, 1024) ?? [];
        var bus = descriptor.Length >= 32 ? BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28)) : 0;
        var vendor = descriptor.Length >= 16 ? Win32.AsciiZ(descriptor, BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(12))) : "";
        var product = descriptor.Length >= 20 ? Win32.AsciiZ(descriptor, BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(16))) : "";
        var serial = descriptor.Length >= 28 ? Win32.AsciiZ(descriptor, BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(24))) : "";
        var attributes = Win32.Query(device, Win32.IoctlDiskGetDiskAttributes, initialSize: 16);
        var flags = attributes is { Length: >= 16 } ? BinaryPrimitives.ReadUInt64LittleEndian(attributes.AsSpan(8)) : 0;
        var model = string.Join(" ", new[] { vendor, product }.Where(s => s.Length > 0));
        return new DiskProbe(number, size, model.Length > 0 ? model : $"Disk {number}", serial, bus,
            IsOffline: (flags & 1) != 0, IsReadOnly: (flags & 2) != 0, layout, SectorSize: sectorSize > 0 ? sectorSize : 512);
    }

    public static List<VolumeProbe> ProbeVolumes() => ProbeVolumes(out _);

    internal static VolumeProbe? ProbeVolume(string name) => ProbeVolumes([name], out _).SingleOrDefault();

    private static List<VolumeProbe> ProbeVolumes(out HashSet<int> dynamicDisks) =>
        ProbeVolumes(Win32.Volumes(), out dynamicDisks);

    private static List<VolumeProbe> ProbeVolumes(IEnumerable<string> names, out HashSet<int> dynamicDisks)
    {
        dynamicDisks = [];
        var bootVolume = Win32.VolumeNameOf(Path.GetPathRoot(Environment.SystemDirectory) ?? "");
        var result = new List<VolumeProbe>();
        foreach (var name in names)
        {
            using var device = Win32.OpenDevice(name.TrimEnd('\\'));
            if (device == null) continue;
            var extents = Win32.Query(device, Win32.IoctlVolumeGetVolumeDiskExtents, initialSize: 8 + 24 * 4);
            // A volume spanning several extents is dynamic or Storage Spaces; it is never mapped onto one partition.
            if (extents == null || extents.Length < 32) continue;
            var count = BinaryPrimitives.ReadInt32LittleEndian(extents);
            if (count != 1)
            {
                if (count > 1 && extents.Length >= 12)
                    dynamicDisks.Add(BinaryPrimitives.ReadInt32LittleEndian(extents.AsSpan(8)));
                continue;
            }
            var disk = BinaryPrimitives.ReadInt32LittleEndian(extents.AsSpan(8));
            var offset = BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(16));
            var info = Win32.VolumeInformation(name);
            var cluster = 0;
            if (info?.FileSystem == "NTFS")
            {
                using var readable = Win32.OpenDevice(name.TrimEnd('\\'), read: true);
                if (readable != null)
                {
                    try { cluster = NativeNtfsSize.Read(readable).BytesPerCluster; }
                    catch (InvalidOperationException) { }
                }
            }
            result.Add(new VolumeProbe(disk, offset, name, Win32.MountPaths(name), info?.Label ?? "", info?.FileSystem ?? "",
                info is { } i ? i.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                info == null ? 0 : Win32.FreeBytes(name) ?? 0, Encryption(disk, offset),
                bootVolume != null && string.Equals(bootVolume, name, StringComparison.OrdinalIgnoreCase),
                PeEnvironment.IsPeMedia(name), Directory.Exists(name + @"Windows\System32"),
                File.Exists(name + "bootmgr") || File.Exists(name + @"EFI\Microsoft\Boot\BCD"), cluster));
        }
        return result;
    }

    /// <summary>
    /// Reads the partition's first sector from the physical disk. Reading through the volume would return
    /// BitLocker's decrypted view, so only the raw sector shows that the volume is encrypted.
    /// </summary>
    private static string Encryption(int disk, long offset)
    {
        try
        {
            using var handle = File.OpenHandle($@"\\.\PhysicalDrive{disk}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sector = new byte[4096];
            var read = RandomAccess.Read(handle, sector, offset);
            if (read < 512) return "Unknown";
            return sector.AsSpan(3, 8).SequenceEqual(BitLockerSignature) ? "Encrypted" : "None";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Unknown"; }
    }

    public static string OccupiedLetters()
    {
        var mask = Win32.GetLogicalDrives();
        var letters = new StringBuilder();
        for (var i = 0; i < 26; i++)
            if ((mask & (1u << i)) != 0) letters.Append((char)('A' + i));
        return letters.ToString();
    }
}
