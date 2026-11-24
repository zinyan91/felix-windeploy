namespace Felix.WindowSetup.Core;

public enum SummaryEffect
{
    Format,
    AddBootFiles,
    SetActive,
    Keep,
    Create,
    Erase
}

public sealed record SummaryLine(
    PartitionRole? Role,
    TargetPartitionFacts? Partition,
    IReadOnlyList<SummaryEffect> Effects,
    long PlannedSize,
    string PlannedFileSystem);

public sealed record TargetPlanSummary(
    IReadOnlyList<SummaryLine> Lines,
    IReadOnlyList<TargetPartitionFacts> Kept,
    IReadOnlyList<TargetPartitionFacts> Lost,
    IReadOnlyList<TargetPartitionFacts> LostBitLocker);

public static class TargetSummaries
{
    public static TargetPlanSummary ForPartitionMode(TargetDiskFacts disk, RoleSelection roles)
    {
        var lines = new List<SummaryLine>();
        AddWindowsSystem(roles, lines);
        if (roles.Recovery is not null)
        {
            lines.Add(new(
                PartitionRole.Recovery,
                roles.Recovery,
                [SummaryEffect.Format],
                roles.Recovery.Size,
                "NTFS"));
        }

        var selected = new[] { roles.Windows, roles.System, roles.Recovery }
            .Where(partition => partition is not null)
            .Cast<TargetPartitionFacts>()
            .ToHashSet();
        var kept = disk.Partitions.Where(partition =>
                !partition.IsUnallocated && !selected.Contains(partition))
            .ToArray();
        lines.AddRange(kept.Select(partition =>
            new SummaryLine(null, partition, [SummaryEffect.Keep], partition.Size, partition.FileSystem)));
        return new(lines, kept, [], []);

        void AddWindowsSystem(RoleSelection selection, ICollection<SummaryLine> target)
        {
            if (selection.Windows == selection.System)
            {
                var effects = new List<SummaryEffect> { SummaryEffect.Format, SummaryEffect.AddBootFiles };
                if (diskStyleIsBios(disk))
                {
                    effects.Add(SummaryEffect.SetActive);
                }
                target.Add(new SummaryLine(PartitionRole.Windows, selection.Windows, effects, selection.Windows.Size, "NTFS"));
                return;
            }

            target.Add(new SummaryLine(PartitionRole.Windows, selection.Windows, [SummaryEffect.Format], selection.Windows.Size, "NTFS"));
            var systemEffects = new List<SummaryEffect> { SummaryEffect.AddBootFiles };
            if (diskStyleIsBios(disk) && !selection.System.IsActive)
            {
                systemEffects.Add(SummaryEffect.SetActive);
            }
            target.Add(new SummaryLine(PartitionRole.System, selection.System, systemEffects, selection.System.Size, selection.System.FileSystem));
        }

        static bool diskStyleIsBios(TargetDiskFacts target) =>
            target.Style.Equals("MBR", StringComparison.OrdinalIgnoreCase);
    }

    public static TargetPlanSummary ForEraseMode(TargetDiskFacts disk, FirmwareMode firmware)
    {
        var lines = new List<SummaryLine>();
        var windowsSize = Math.Max(0, disk.Size -
            (firmware == FirmwareMode.UefiGpt
                ? (InstallDiskLayoutBuilder.EfiSizeMiB + InstallDiskLayoutBuilder.MsrSizeMiB + InstallDiskLayoutBuilder.RecoverySizeMiB) * 1024L * 1024
                : (InstallDiskLayoutBuilder.BiosSystemSizeMiB + InstallDiskLayoutBuilder.RecoverySizeMiB) * 1024L * 1024));
        if (firmware == FirmwareMode.UefiGpt)
        {
            lines.Add(new(PartitionRole.System, null, [SummaryEffect.Create], InstallDiskLayoutBuilder.EfiSizeMiB * 1024L * 1024, "FAT32"));
            lines.Add(new(null, null, [SummaryEffect.Create], InstallDiskLayoutBuilder.MsrSizeMiB * 1024L * 1024, "MSR"));
        }
        else
        {
            lines.Add(new(PartitionRole.System, null, [SummaryEffect.Create, SummaryEffect.SetActive], InstallDiskLayoutBuilder.BiosSystemSizeMiB * 1024L * 1024, "NTFS"));
        }

        lines.Add(new(PartitionRole.Windows, null, [SummaryEffect.Create], windowsSize, "NTFS"));
        lines.Add(new(PartitionRole.Recovery, null, [SummaryEffect.Create], InstallDiskLayoutBuilder.RecoverySizeMiB * 1024L * 1024, "NTFS"));
        var lost = disk.Partitions.Where(partition => !partition.IsUnallocated).ToArray();
        return new(lines, [], lost, lost.Where(partition => partition.IsBitLocker).ToArray());
    }
}
