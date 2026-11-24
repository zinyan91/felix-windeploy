namespace Felix.Partition.Services.Native;

public sealed record DiskProbe(int Number, long Size, string Model, string Serial, int BusType, bool IsOffline, bool IsReadOnly,
    DriveLayout? Layout, int SectorSize = 512);

public sealed record VolumeProbe(int DiskNumber, long Offset, string Name, IReadOnlyList<string> Paths, string Label, string FileSystem,
    string Serial, long Free, string Encryption, bool IsBoot, bool IsPeMedia = false, bool HasWindows = false, bool HasBootManager = false,
    int BytesPerCluster = 0);

/// <summary>Turns raw device facts into the storage model. Pure: no device access.</summary>
public static class StorageMapping
{
    public static readonly Guid EfiType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid ReservedType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid BasicDataType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid RecoveryType = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    private const ulong GptReadOnly = 1UL << 60;
    private const ulong GptHidden = 1UL << 62;

    public static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File Backed Virtual",
        16 => "Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "Unknown"
    };

    public static string GptTypeName(Guid type) =>
        type == EfiType ? "System" : type == ReservedType ? "Reserved" : type == BasicDataType ? "Basic" : type == RecoveryType ? "Recovery" : "Unknown";

    /// <summary>Stable within a session and across a write that keeps the partition table: GPT disk GUID or MBR signature, plus serial and size.</summary>
    public static string DiskId(DiskProbe disk) => disk.Layout?.Style switch
    {
        "GPT" => $"GPT:{disk.Layout.GptDiskId:D}|{disk.Serial}|{disk.Size}",
        "MBR" => $"MBR:{disk.Layout.MbrSignature:X8}|{disk.Serial}|{disk.Size}",
        _ => $"RAW:{disk.Serial}|{disk.Size}|{disk.Number}"
    };

    /// <param name="unmountedEncryption">Reads the raw first sector of a partition Windows did not mount (hidden or not a data type).</param>
    public static StorageSnapshot Build(IEnumerable<DiskProbe> disks, IReadOnlyCollection<VolumeProbe> volumes, string occupiedLetters,
        Func<int, long, string>? unmountedEncryption = null, IReadOnlySet<int>? dynamicDisks = null)
    {
        var result = new List<StorageDisk>();
        foreach (var probe in disks.OrderBy(d => d.Number))
        {
            var style = probe.Layout?.Style ?? "Unknown";
            var parts = new List<StoragePartition>();
            if (probe.Layout != null && !probe.IsOffline && style is "GPT" or "MBR")
                foreach (var e in probe.Layout.Entries.OrderBy(e => e.Offset))
                {
                    var volume = volumes.FirstOrDefault(v => v.DiskNumber == probe.Number && v.Offset == e.Offset);
                    var part = Partition(style, e, volume);
                    if (e.IsLogical)
                        part = part with { EbrOffset = probe.Layout.EbrOffsets.Where(o => o < e.Offset).DefaultIfEmpty(0).Max() };
                    if (volume == null && unmountedEncryption != null)
                        part = part with { Encryption = unmountedEncryption(probe.Number, e.Offset) };
                    parts.Add(part);
                }
            result.Add(new StorageDisk
            {
                Number = probe.Number, Id = DiskId(probe), Model = probe.Model, Serial = probe.Serial, Size = probe.Size, Style = style,
                Bus = BusName(probe.BusType), IsOffline = probe.IsOffline, IsReadOnly = probe.IsReadOnly, Partitions = parts,
                ExtendedOffset = probe.Layout?.ExtendedOffset ?? 0, ExtendedSize = probe.Layout?.ExtendedSize ?? 0,
                EbrOffsets = probe.Layout?.EbrOffsets.ToList() ?? [],
                HoldsPeMedia = volumes.Any(v => v.DiskNumber == probe.Number && v.IsPeMedia),
                IsDynamic = dynamicDisks?.Contains(probe.Number) == true,
                SectorSize = probe.SectorSize
            });
        }
        // Two clones of one disk share GUID and serial; keep identities unique so a target never matches both.
        foreach (var group in result.GroupBy(d => d.Id).Where(g => g.Count() > 1).ToList())
            foreach (var disk in group)
                result[result.IndexOf(disk)] = disk with { Id = disk.Id + "#" + disk.Number };
        var snapshot = new StorageSnapshot { Disks = result, OccupiedLetters = occupiedLetters };
        StorageSnapshotRules.Check(snapshot);
        return snapshot;
    }

    private static StoragePartition Partition(string style, LayoutEntry e, VolumeProbe? v)
    {
        var letter = v?.Paths.FirstOrDefault(p => p.Length == 3 && p[1] == ':' && p[2] == '\\') is string root ? root[..1].ToUpperInvariant() : "";
        var folderMount = v?.Paths.Any(p => !(p.Length == 3 && p[1] == ':' && p[2] == '\\')) ?? false;
        var gpt = style == "GPT";
        return new StoragePartition
        {
            Number = e.Number, Offset = e.Offset, Size = e.Length, Free = v?.Free ?? 0,
            Guid = gpt ? "{" + e.GptId.ToString("D") + "}" : "",
            Label = v?.Label ?? "", Letter = letter, FileSystem = v?.FileSystem ?? "", VolumeSerial = v?.Serial ?? "",
            Type = gpt ? GptTypeName(e.GptType) : MbrTypes.Name(e.MbrType),
            GptType = gpt ? "{" + e.GptType.ToString("D") + "}" : "",
            GptName = gpt ? e.GptName : "",
            MbrType = gpt ? "" : MbrTypes.Name(e.MbrType),
            MbrTypeByte = gpt ? (byte)0 : e.MbrType,
            GptAttributes = gpt ? e.GptAttributes : 0,
            IsHidden = gpt ? (e.GptAttributes & GptHidden) != 0 : MbrTypes.IsHiddenData(e.MbrType),
            IsLogical = e.IsLogical,
            IsBoot = v?.IsBoot ?? false,
            IsSystem = gpt ? e.GptType == EfiType : e.MbrBoot,
            IsActive = !gpt && e.MbrBoot,
            IsReadOnly = gpt && (e.GptAttributes & GptReadOnly) != 0,
            HasFolderMount = folderMount,
            Encryption = v?.Encryption ?? "Unknown",
            IsPeMedia = v?.IsPeMedia ?? false,
            HasWindows = v?.HasWindows ?? false,
            HasBootManager = v?.HasBootManager ?? false,
            BytesPerCluster = v?.BytesPerCluster ?? 0
        };
    }
}
