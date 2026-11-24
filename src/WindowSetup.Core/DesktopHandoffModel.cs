using System.Text.Json;
using System.Text.Json.Serialization;

namespace Felix.WindowSetup.Core;

public enum SetupExecutionMode
{
    DirectWinPe,
    DesktopHandoff
}

public sealed record SourcePartitionSignature(
    int DiskNumber,
    string DiskId,
    string DiskSerial,
    long PartitionOffset,
    long PartitionSize,
    string RelativePathFromRoot);

public sealed record DesktopHandoffSession(
    int SchemaVersion,
    string SessionId,
    DateTime CreatedUtc,
    WindowSetupRequest Request,
    string? RelativeSourceImagePath = null,
    SourcePartitionSignature? ExternalSourceSignature = null,
    string? RelativeAnswerFilePath = null,
    string? RelativeStagedDriversDir = null,
    string? RelativeStagedWifiFile = null,
    string? RelativeStagedPrinterFile = null,
    int StagedDriverCount = 0,
    IReadOnlyDictionary<RemoteSupportAppKind, string>? RelativeStagedRemoteInstallers = null,
    string? RelativeStagedFirstDesktopSource = null,
    string? BcdOsId = null,
    string? BcdDeviceId = null,
    bool AutoRebootOnComplete = true,
    bool CreatedFelixWork = false,
    long? FelixWorkOffset = null,
    WinPeRecordingOptions? Recording = null,
    int? RescueUsbDiskNumber = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DesktopHandoffSession FromJson(string json) =>
        JsonSerializer.Deserialize<DesktopHandoffSession>(json, JsonOptions)
        ?? throw new InvalidDataException("Handoff session JSON is empty or invalid.");
}

public sealed record StagingVolumeCandidate(
    char Letter,
    long FreeBytes,
    string FileSystem,
    bool IsBitLocker,
    bool IsRemovable,
    bool IsOnTargetDisk,
    bool IsTargetWindowsPartition,
    bool HoldsSourceImage);

public sealed record StagingVolumeDecision(
    char? SelectedLetter,
    string? Error,
    bool RequiresFelixWorkShrink = false,
    int? ShrinkDiskNumber = null,
    int? ShrinkPartitionNumber = null,
    int ShrinkSizeMiB = 0)
{
    public bool Succeeded => (SelectedLetter is not null || RequiresFelixWorkShrink) && string.IsNullOrWhiteSpace(Error);
}

public static class DesktopHandoffPlanner
{
    public const string BcdEntryTitle = "Felix WinDeploy";

    public static StagingVolumeDecision PlanStagingOrShrink(
        IReadOnlyList<StagingVolumeCandidate> candidates,
        TargetDiskFacts? targetDisk,
        TargetPartitionFacts? targetWindowsPartition,
        InstallTargetMode targetMode,
        long requiredBytes)
    {
        var primary = SelectStagingVolume(candidates, targetMode, requiredBytes);
        if (primary.Succeeded)
        {
            return primary;
        }

        if (targetMode != InstallTargetMode.ExistingPartition || targetDisk is null || targetWindowsPartition is null)
        {
            return primary;
        }

        if (targetWindowsPartition.IsBitLocker)
        {
            return new StagingVolumeDecision(
                null,
                "Cannot shrink BitLocker-encrypted target partition to create temporary FELIXWORK partition.");
        }

        if (!targetWindowsPartition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        if (targetDisk.Style.Equals("MBR", StringComparison.OrdinalIgnoreCase)
            && targetDisk.Partitions.Count(p => !p.IsUnallocated) >= 4)
        {
            return new StagingVolumeDecision(
                null,
                "MBR disk already has 4 partitions; cannot create temporary FELIXWORK partition.");
        }

        var shrinkMiB = Math.Max(2048, (int)Math.Ceiling(requiredBytes / (1024d * 1024d)) + 512);
        var shrinkBytes = shrinkMiB * 1024L * 1024L;
        if (targetWindowsPartition.Size - shrinkBytes < 20L * 1024 * 1024 * 1024
            || targetWindowsPartition.FreeBytes < shrinkBytes + (2048L * 1024 * 1024))
        {
            return primary;
        }

        return new StagingVolumeDecision(
            null,
            null,
            RequiresFelixWorkShrink: true,
            ShrinkDiskNumber: targetDisk.Number,
            ShrinkPartitionNumber: targetWindowsPartition.Number,
            ShrinkSizeMiB: shrinkMiB);
    }

    public static StagingVolumeDecision SelectStagingVolume(
        IReadOnlyList<StagingVolumeCandidate> candidates,
        InstallTargetMode targetMode,
        long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var eligible = candidates
            .Where(c => !c.IsRemovable
                && !c.IsBitLocker
                && c.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                && !c.IsTargetWindowsPartition
                && (targetMode != InstallTargetMode.EraseDisk || !c.IsOnTargetDisk)
                && c.FreeBytes >= requiredBytes)
            .OrderByDescending(c => c.HoldsSourceImage)
            .ThenByDescending(c => c.FreeBytes)
            .ToList();

        if (eligible.Count > 0)
        {
            return new StagingVolumeDecision(char.ToUpperInvariant(eligible[0].Letter), null);
        }

        var requiredGiB = requiredBytes / (1024d * 1024d * 1024d);
        return new StagingVolumeDecision(
            null,
            $"No eligible NTFS staging volume found with at least {requiredGiB:0.0} GB free space (non-USB, non-BitLocker, not the target Windows partition).");
    }

    public static IReadOnlyList<IReadOnlyList<string>> BuildBcdRamdiskCommands(
        char stagingDriveLetter,
        string wimRelativePath,
        string sdiRelativePath,
        FirmwareMode firmware,
        string osId,
        string deviceId,
        bool bootOnce = true)
    {
        var drive = $"{char.ToUpperInvariant(stagingDriveLetter)}:";
        var wim = wimRelativePath.StartsWith('\\') ? wimRelativePath : "\\" + wimRelativePath;
        var sdi = sdiRelativePath.StartsWith('\\') ? sdiRelativePath : "\\" + sdiRelativePath;
        var winload = firmware == FirmwareMode.UefiGpt ? "winload.efi" : "winload.exe";
        var ramdisk = $"ramdisk=[{drive}]{wim},{deviceId}";

        var commands = new List<IReadOnlyList<string>>
        {
            new[] { "/create", deviceId, "/d", BcdEntryTitle, "/device" },
            new[] { "/set", deviceId, "ramdisksdidevice", $"partition={drive}" },
            new[] { "/set", deviceId, "ramdisksdipath", sdi },
            new[] { "/create", osId, "/d", BcdEntryTitle, "/application", "osloader" },
            new[] { "/set", osId, "device", ramdisk },
            new[] { "/set", osId, "osdevice", ramdisk },
            new[] { "/set", osId, "path", $@"\windows\system32\boot\{winload}" },
            new[] { "/set", osId, "systemroot", @"\windows" },
            new[] { "/set", osId, "detecthal", "Yes" },
            new[] { "/set", osId, "winpe", "Yes" },
            new[] { "/set", osId, "highestmode", "Yes" }
        };

        if (bootOnce)
        {
            commands.Add(["/bootsequence", osId]);
        }
        else
        {
            commands.Add(["/displayorder", osId, "/addlast"]);
        }

        return commands;
    }

    public static IReadOnlyList<IReadOnlyList<string>> BuildBcdCleanupCommands(
        string? osId,
        string? deviceId,
        string? storePath = null)
    {
        var prefix = string.IsNullOrWhiteSpace(storePath)
            ? Array.Empty<string>()
            : ["/store", storePath];

        var commands = new List<IReadOnlyList<string>>();
        if (!string.IsNullOrWhiteSpace(osId))
        {
            commands.Add([.. prefix, "/delete", osId, "/cleanup"]);
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            commands.Add([.. prefix, "/delete", deviceId]);
        }

        return commands;
    }

    public static WimlibCommand BuildWimlibBootPatchCommand(
        string wimlibPath,
        string bootWimPath,
        string patchedBootCmdPath,
        string? patchedWinpeshlIniPath = null)
    {
        var quotedSrc = patchedBootCmdPath.Contains(' ') ? $"\"{patchedBootCmdPath}\"" : patchedBootCmdPath;
        var args = new List<string>
        {
            "update",
            bootWimPath,
            "1",
            $"--command=add {quotedSrc} \\Windows\\System32\\felixpe-boot.cmd"
        };

        if (!string.IsNullOrWhiteSpace(patchedWinpeshlIniPath))
        {
            var quotedIni = patchedWinpeshlIniPath.Contains(' ') ? $"\"{patchedWinpeshlIniPath}\"" : patchedWinpeshlIniPath;
            args.Add($"--command=add {quotedIni} \\Windows\\System32\\winpeshl.ini");
        }

        return new WimlibCommand(wimlibPath, args);
    }

    public static string? ResolveSessionRelativePath(string sessionDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = Path.GetFullPath(sessionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(sessionDirectory, relativePath));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}
