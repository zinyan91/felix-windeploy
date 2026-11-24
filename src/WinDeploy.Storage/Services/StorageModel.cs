using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Felix.Partition.Services;

public sealed record StoragePartition
{
    public int Number { get; init; }
    public long Offset { get; init; }
    public long Size { get; init; }
    public long Free { get; init; }
    public string Guid { get; init; } = "";
    public string Label { get; init; } = "";
    public string Letter { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string VolumeSerial { get; init; } = "";
    public string Type { get; init; } = "";
    public string GptType { get; init; } = "";
    public string GptName { get; init; } = "";
    public string MbrType { get; init; } = "";
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public bool IsReadOnly { get; init; }
    public bool HasFolderMount { get; init; }
    public string Encryption { get; init; } = "Unknown";
    public bool Unallocated { get; init; }
    public bool IsHidden { get; init; }
    public bool IsLogical { get; init; }
    public long EbrOffset { get; init; }
    public byte MbrTypeByte { get; init; }
    public ulong GptAttributes { get; init; }
    public FreeKind FreeKind { get; init; }
    public bool IsPeMedia { get; init; }
    public bool HasWindows { get; init; }
    public bool HasBootManager { get; init; }
    public string Lineage { get; init; } = "";
    public int BytesPerCluster { get; init; }
}

public sealed record StorageDisk
{
    public int Number { get; init; }
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Style { get; init; } = "";
    public string Bus { get; init; } = "";
    public long Size { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public long ExtendedOffset { get; init; }
    public long ExtendedSize { get; init; }
    public List<long> EbrOffsets { get; init; } = [];
    public bool HoldsPeMedia { get; init; }
    public bool IsDynamic { get; init; }
    public int SectorSize { get; init; } = 512;
    public List<StoragePartition> Partitions { get; init; } = new();
}

public sealed record StorageSnapshot
{
    public List<StorageDisk> Disks { get; init; } = new();
    public string OccupiedLetters { get; init; } = "";
}

public enum FreeKind { None, Primary, Logical, PrimaryOrLogical }

public static class StorageSnapshotRules
{
    /// <summary>A snapshot that breaks geometry is a read failure, never a safe-looking disk.</summary>
    public static void Check(StorageSnapshot snapshot)
    {
        if (snapshot.Disks.GroupBy(d => d.Number).Any(g => g.Count() > 1) || snapshot.Disks.GroupBy(d => d.Id).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Partition_Error_Snapshot");
        foreach (var disk in snapshot.Disks)
        {
            if (disk.Size <= 0 || disk.Partitions.Any(p => p.Size <= 0 || p.Offset < 0 || p.Offset > disk.Size - p.Size))
                throw new InvalidOperationException("Partition_Error_Snapshot");
            long end = 0;
            foreach (var p in disk.Partitions.OrderBy(p => p.Offset))
            {
                if (p.Offset < end) throw new InvalidOperationException("Partition_Error_Snapshot");
                end = p.Offset + p.Size;
            }
        }
    }
}

/// <summary>Where the tool runs and whether the user asked for expert actions; one code path, two rule sets.</summary>
public sealed record PartitionPolicy(bool IsPe, bool Expert)
{
    public static readonly PartitionPolicy Desktop = new(false, false);
}
