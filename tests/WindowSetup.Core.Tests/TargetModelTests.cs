using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Core.Tests;

public static class TargetModelTests
{
    public static void Run()
    {
        var esp = Part(1, 260L << 20, "EFI", "FAT32", isEfi: true);
        var windows = Part(3, 60L << 30, "Windows", "NTFS", hasWindows: true);
        var recovery = Part(4, 1L << 30, "Recovery", "NTFS", isRecovery: true);
        var data = Part(5, 20L << 30, "Data", "NTFS");
        var gpt = Disk("GPT", [esp, windows, recovery, data]);
        Assert(TargetRules.Evaluate(gpt, new HashSet<char>(), true) == DiskBlockReason.None, "disk allowed");
        var roles = PartitionRoles.Suggest(gpt, windows, FirmwareMode.UefiGpt);
        Assert(roles is { System.Label: "EFI", Recovery.Label: "Recovery" }, "roles suggested");
        var summary = TargetSummaries.ForPartitionMode(gpt, roles!);
        Assert(summary.Kept.SequenceEqual([data]), "data kept");
        var mbrPart = Part(1, 60L << 30, "Windows", "NTFS", hasWindows: true);
        var mbr = Disk("MBR", [mbrPart]);
        var mbrRoles = PartitionRoles.Suggest(mbr, mbrPart, FirmwareMode.BiosMbr);
        Assert(mbrRoles is { System: not null } && mbrRoles.System == mbrRoles.Windows, "bios shared system");
        var mbrSummary = TargetSummaries.ForPartitionMode(mbr, mbrRoles!);
        Assert(mbrSummary.Lines.Count == 1 &&
               mbrSummary.Lines[0].Effects.Contains(SummaryEffect.Format) &&
               mbrSummary.Lines[0].Effects.Contains(SummaryEffect.AddBootFiles) &&
               mbrSummary.Lines[0].Effects.Contains(SummaryEffect.SetActive),
            "bios shared single summary line");

        var locked = Part(1, 60L << 30, "Secret", "NTFS") with { IsBitLocker = true };
        var lockedDisk = Disk("GPT", [locked]);
        Assert(!TargetRules.CanTakeRole(lockedDisk, locked, PartitionRole.Windows, FirmwareMode.UefiGpt), "bitlocker blocked in partition mode");
        Assert(TargetRules.CanErase(lockedDisk), "bitlocker allowed in erase mode");
        var erase = TargetSummaries.ForEraseMode(gpt, FirmwareMode.UefiGpt);
        Assert(erase.Lines.Count == 4 && erase.Lines[0].PlannedSize == 260L << 20, "erase summary");
        Assert(TargetSummaries.ForEraseMode(lockedDisk, FirmwareMode.UefiGpt).LostBitLocker.SequenceEqual([locked]), "erase tracks bitlocker");
    }

    private static TargetDiskFacts Disk(string style, IReadOnlyList<TargetPartitionFacts> partitions) =>
        new(0, "id", "serial", "Virtual Disk", "NVMe", style, 128L << 30,
            false, false, false, false, false, true, partitions);

    private static TargetPartitionFacts Part(
        int number,
        long size,
        string label,
        string fileSystem,
        bool isEfi = false,
        bool isRecovery = false,
        bool hasWindows = false) =>
        new(number, number * 1L << 30, size, "", label, fileSystem, $"g{number}", $"s{number}",
            isEfi, false, isRecovery, false, false, false, hasWindows);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
