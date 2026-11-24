using System.Globalization;

namespace Felix.WindowSetup.Core;

public enum RescueUsbMode
{
    None,
    NonDestructiveFat32,
    FormatDisk
}

public sealed record RescueUsbCandidate(
    int DiskNumber,
    string DiskId,
    string Serial,
    string Model,
    long SizeBytes,
    RescueUsbMode PreparationMode,
    char? ExistingFat32Letter,
    string? BlockReason = null)
{
    public bool IsNone => DiskNumber < 0;

    public bool IsEligible => DiskNumber >= 0 && string.IsNullOrWhiteSpace(BlockReason);

    public bool RequiresXoaConfirmation =>
        PreparationMode == RescueUsbMode.FormatDisk && SizeBytes >= 128L * 1024 * 1024 * 1024;

    public string ConfirmationToken => RequiresXoaConfirmation ? "XOA" : "Y";

    public string DisplayText => DiskNumber < 0
        ? "Do not create"
        : $"Disk {DiskNumber} · {Model} · {SizeBytes / (1024d * 1024 * 1024):0.#} GB";
}

public static class RescueUsbPlanner
{
    public const long MinimumFreeFat32Bytes = 1_200_000_000L;
    public const long MinimumUsbDiskBytes = 2L * 1024 * 1024 * 1024;

    public static RescueUsbCandidate EvaluateUsbDisk(
        TargetDiskFacts disk,
        IReadOnlySet<char> protectedLetters)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(protectedLetters);

        string? blockReason = null;
        if (!disk.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            blockReason = "Not a USB drive.";
        }
        else if (disk.HoldsPeMedia)
        {
            blockReason = "Contains active FelixPE media.";
        }
        else if (disk.IsReadOnly)
        {
            blockReason = "Disk is read-only.";
        }
        else if (disk.IsOffline)
        {
            blockReason = "Disk is offline.";
        }
        else if (disk.Size < MinimumUsbDiskBytes)
        {
            blockReason = "USB drive is smaller than 2 GB.";
        }
        else if (disk.Partitions.Any(p =>
                     !string.IsNullOrWhiteSpace(p.Letter)
                     && protectedLetters.Contains(char.ToUpperInvariant(p.Letter.TrimEnd(':')[0]))))
        {
            blockReason = "Contains Felix WinDeploy or Windows source files.";
        }

        var firstPart = disk.Partitions.FirstOrDefault(p => !p.IsUnallocated);
        if (blockReason is null
            && firstPart is not null
            && firstPart.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
            && !firstPart.IsBitLocker
            && !string.IsNullOrWhiteSpace(firstPart.Letter)
            && firstPart.FreeBytes >= MinimumFreeFat32Bytes)
        {
            var letter = char.ToUpperInvariant(firstPart.Letter.TrimEnd(':')[0]);
            return new RescueUsbCandidate(
                disk.Number,
                disk.Id,
                disk.Serial,
                disk.Model,
                disk.Size,
                RescueUsbMode.NonDestructiveFat32,
                letter);
        }

        return new RescueUsbCandidate(
            disk.Number,
            disk.Id,
            disk.Serial,
            disk.Model,
            disk.Size,
            RescueUsbMode.FormatDisk,
            null,
            blockReason);
    }

    public static string BuildFormatUsbScript(int diskNumber, long sizeBytes, char fat32Letter)
    {
        if (diskNumber is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(diskNumber));
        }

        var upper = char.ToUpperInvariant(fat32Letter);
        if (upper is < 'D' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(fat32Letter));
        }

        var lines = new List<string>
        {
            $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}",
            "clean",
            "convert gpt"
        };

        if (sizeBytes > 6L * 1024 * 1024 * 1024)
        {
            lines.Add("create partition primary size=3072");
            lines.Add("format quick fs=fat32 label=FELIXBOOT");
            lines.Add($"assign letter={upper}");
            lines.Add("create partition primary");
            lines.Add("format quick fs=exfat label=FELIXDATA");
        }
        else
        {
            lines.Add("create partition primary");
            lines.Add("format quick fs=fat32 label=FELIXBOOT");
            lines.Add($"assign letter={upper}");
        }

        return string.Join('\n', lines) + "\n";
    }

    public static IReadOnlyList<IReadOnlyList<string>> BuildUsbUefiBcdCommands(
        string bcdStorePath,
        string osId,
        string deviceId)
    {
        return
        [
            ["/store", bcdStorePath, "/create", "{bootmgr}", "/d", "Windows Boot Manager"],
            ["/store", bcdStorePath, "/create", deviceId, "/d", "Felix Rescue WinPE", "/device"],
            ["/store", bcdStorePath, "/set", deviceId, "ramdisksdidevice", "boot"],
            ["/store", bcdStorePath, "/set", deviceId, "ramdisksdipath", @"\FelixBootRescue\boot.sdi"],
            ["/store", bcdStorePath, "/create", osId, "/d", "Felix Rescue Boot", "/application", "osloader"],
            ["/store", bcdStorePath, "/set", osId, "device", $@"ramdisk=[boot]\FelixBootRescue\boot.wim,{deviceId}"],
            ["/store", bcdStorePath, "/set", osId, "osdevice", $@"ramdisk=[boot]\FelixBootRescue\boot.wim,{deviceId}"],
            ["/store", bcdStorePath, "/set", osId, "path", @"\windows\system32\boot\winload.efi"],
            ["/store", bcdStorePath, "/set", osId, "systemroot", @"\windows"],
            ["/store", bcdStorePath, "/set", osId, "detecthal", "Yes"],
            ["/store", bcdStorePath, "/set", osId, "winpe", "Yes"],
            ["/store", bcdStorePath, "/set", osId, "highestmode", "Yes"],
            ["/store", bcdStorePath, "/set", "{bootmgr}", "default", osId],
            ["/store", bcdStorePath, "/set", "{bootmgr}", "displayorder", osId],
            ["/store", bcdStorePath, "/set", "{bootmgr}", "timeout", "5"]
        ];
    }
}
