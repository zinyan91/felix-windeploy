namespace Felix.WindowSetup.Core;

public sealed record TargetPartitionFacts(
    int Number,
    long Offset,
    long Size,
    string Letter,
    string Label,
    string FileSystem,
    string Guid,
    string VolumeSerial,
    bool IsEfi,
    bool IsMsr,
    bool IsRecovery,
    bool IsActive,
    bool IsBitLocker,
    bool IsUnallocated,
    bool HasWindows,
    long FreeBytes = 0);

public sealed record TargetDiskFacts(
    int Number,
    string Id,
    string Serial,
    string Model,
    string Bus,
    string Style,
    long Size,
    bool HoldsPeMedia,
    bool RunsWindows,
    bool IsReadOnly,
    bool IsOffline,
    bool IsDynamic,
    bool IsVirtual,
    IReadOnlyList<TargetPartitionFacts> Partitions);

public enum DiskBlockReason
{
    None,
    FelixPe,
    RunningWindows,
    ReadOnly,
    Offline,
    Dynamic,
    HoldsSetupFiles,
    DesktopPhysicalDisk
}

public enum PartitionModeBlock
{
    None,
    StyleMismatch,
    NoSystemPartition
}

public enum PartitionRole
{
    Windows,
    System,
    Recovery
}

public sealed record RoleSelection(
    TargetPartitionFacts Windows,
    TargetPartitionFacts System,
    TargetPartitionFacts? Recovery);

public static class TargetRules
{
    public static DiskBlockReason EvaluateForMode(
        TargetDiskFacts disk,
        IReadOnlySet<char> setupFileLetters,
        bool isWinPe,
        InstallTargetMode mode,
        bool allowDesktopHandoff) =>
        Evaluate(
            disk,
            setupFileLetters,
            isWinPe,
            allowDesktopHandoff && (mode == InstallTargetMode.ExistingPartition || !disk.RunsWindows));

    public static DiskBlockReason Evaluate(
        TargetDiskFacts disk,
        IReadOnlySet<char> setupFileLetters,
        bool isWinPe,
        bool allowDesktopHandoff = false)
    {
        if (disk.HoldsPeMedia)
        {
            return DiskBlockReason.FelixPe;
        }
        if (disk.RunsWindows && !allowDesktopHandoff)
        {
            return DiskBlockReason.RunningWindows;
        }
        if (disk.IsReadOnly)
        {
            return DiskBlockReason.ReadOnly;
        }
        if (disk.IsOffline)
        {
            return DiskBlockReason.Offline;
        }
        if (disk.IsDynamic)
        {
            return DiskBlockReason.Dynamic;
        }
        if (disk.Partitions.Any(partition =>
                !string.IsNullOrWhiteSpace(partition.Letter) &&
                setupFileLetters.Contains(char.ToUpperInvariant(partition.Letter.TrimEnd(':')[0]))))
        {
            var blockWholeDisk = !allowDesktopHandoff ||
                disk.Partitions.All(partition =>
                    partition.IsUnallocated ||
                    partition.IsEfi ||
                    partition.IsMsr ||
                    partition.IsRecovery ||
                    (!string.IsNullOrWhiteSpace(partition.Letter) &&
                     setupFileLetters.Contains(char.ToUpperInvariant(partition.Letter.TrimEnd(':')[0]))));
            if (blockWholeDisk)
            {
                return DiskBlockReason.HoldsSetupFiles;
            }
        }
        if (!isWinPe && !disk.IsVirtual && !allowDesktopHandoff)
        {
            return DiskBlockReason.DesktopPhysicalDisk;
        }
        return DiskBlockReason.None;
    }

    public static PartitionModeBlock PartitionModeBlock(TargetDiskFacts disk, FirmwareMode bootFirmware)
    {
        if (!disk.Style.Equals(bootFirmware == FirmwareMode.UefiGpt ? "GPT" : "MBR", StringComparison.OrdinalIgnoreCase))
        {
            return global::Felix.WindowSetup.Core.PartitionModeBlock.StyleMismatch;
        }

        var hasSystem = disk.Partitions.Any(partition =>
            CanTakeRoleIgnoringSystemPresence(disk, partition, PartitionRole.System, bootFirmware));
        return hasSystem
            ? global::Felix.WindowSetup.Core.PartitionModeBlock.None
            : global::Felix.WindowSetup.Core.PartitionModeBlock.NoSystemPartition;
    }

    public static bool CanTakeRole(
        TargetDiskFacts disk,
        TargetPartitionFacts partition,
        PartitionRole role,
        FirmwareMode bootFirmware,
        bool allowDesktopHandoff = false,
        IReadOnlySet<char>? setupFileLetters = null)
    {
        if (partition.IsUnallocated || partition.IsBitLocker ||
            Evaluate(disk, setupFileLetters ?? new HashSet<char>(), true, allowDesktopHandoff) != DiskBlockReason.None)
        {
            return false;
        }

        if (role == PartitionRole.Windows &&
            setupFileLetters is not null &&
            !string.IsNullOrWhiteSpace(partition.Letter) &&
            setupFileLetters.Contains(char.ToUpperInvariant(partition.Letter.TrimEnd(':')[0])))
        {
            return false;
        }

        var modeBlock = PartitionModeBlock(disk, bootFirmware);
        if (modeBlock == global::Felix.WindowSetup.Core.PartitionModeBlock.StyleMismatch ||
            (modeBlock == global::Felix.WindowSetup.Core.PartitionModeBlock.NoSystemPartition && role != PartitionRole.System))
        {
            return false;
        }

        return CanTakeRoleIgnoringSystemPresence(disk, partition, role, bootFirmware);
    }

    public static bool CanErase(TargetDiskFacts disk) =>
        Evaluate(disk, new HashSet<char>(), true) == DiskBlockReason.None &&
        disk.Size >= InstallDiskLayoutBuilder.MinimumDiskBytes;

    private static bool CanTakeRoleIgnoringSystemPresence(
        TargetDiskFacts disk,
        TargetPartitionFacts partition,
        PartitionRole role,
        FirmwareMode bootFirmware)
    {
        if (partition.IsUnallocated || partition.IsBitLocker)
        {
            return false;
        }

        return role switch
        {
            PartitionRole.Windows =>
                !partition.IsEfi && !partition.IsMsr && !partition.IsRecovery &&
                partition.Size >= 20L * 1024 * 1024 * 1024,
            PartitionRole.System when bootFirmware == FirmwareMode.UefiGpt =>
                partition.IsEfi &&
                partition.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) &&
                partition.Size >= 100L * 1024 * 1024,
            PartitionRole.System =>
                !partition.IsMsr && !partition.IsRecovery &&
                (partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                 partition.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)) &&
                partition.Size >= 100L * 1024 * 1024,
            PartitionRole.Recovery =>
                partition.IsRecovery && partition.Size >= 750L * 1024 * 1024,
            _ => false
        };
    }
}

public static class PartitionRoles
{
    public static RoleSelection? Suggest(
        TargetDiskFacts disk,
        TargetPartitionFacts windows,
        FirmwareMode bootFirmware,
        bool allowDesktopHandoff = false,
        IReadOnlySet<char>? setupFileLetters = null)
    {
        if (!TargetRules.CanTakeRole(disk, windows, PartitionRole.Windows, bootFirmware, allowDesktopHandoff, setupFileLetters))
        {
            return null;
        }

        TargetPartitionFacts? system = bootFirmware == FirmwareMode.UefiGpt
            ? disk.Partitions.FirstOrDefault(partition =>
                TargetRules.CanTakeRole(disk, partition, PartitionRole.System, bootFirmware, allowDesktopHandoff, setupFileLetters))
            : disk.Partitions.FirstOrDefault(partition =>
                partition.IsActive && TargetRules.CanTakeRole(disk, partition, PartitionRole.System, bootFirmware, allowDesktopHandoff, setupFileLetters))
              ?? (TargetRules.CanTakeRole(disk, windows, PartitionRole.System, bootFirmware, allowDesktopHandoff, setupFileLetters) ? windows : null);

        if (system is null)
        {
            return null;
        }

        var recovery = disk.Partitions.FirstOrDefault(partition =>
            TargetRules.CanTakeRole(disk, partition, PartitionRole.Recovery, bootFirmware, allowDesktopHandoff, setupFileLetters));
        return new RoleSelection(windows, system, recovery);
    }

    public static RoleSelection Assign(
        RoleSelection current,
        TargetDiskFacts disk,
        TargetPartitionFacts partition,
        PartitionRole role,
        FirmwareMode bootFirmware,
        bool allowDesktopHandoff = false,
        IReadOnlySet<char>? setupFileLetters = null)
    {
        if (!TargetRules.CanTakeRole(disk, partition, role, bootFirmware, allowDesktopHandoff, setupFileLetters))
        {
            throw new InvalidOperationException($"Partition {partition.Number} cannot be assigned the {role} role.");
        }

        if (role == PartitionRole.Windows)
        {
            return Suggest(disk, partition, bootFirmware, allowDesktopHandoff, setupFileLetters)
                ?? throw new InvalidOperationException("The selected Windows partition has no compatible System partition.");
        }

        return role == PartitionRole.System
            ? current with { System = partition }
            : current with { Recovery = partition };
    }
}
