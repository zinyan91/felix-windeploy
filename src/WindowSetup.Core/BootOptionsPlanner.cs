namespace Felix.WindowSetup.Core;

public static class RecoverySizeCalculator
{
    public const int HeadroomMiB = 300;

    public static int CalculateRequiredMiB(long winreFileBytes)
    {
        var computed = winreFileBytes <= 0
            ? InstallDiskLayoutBuilder.RecoverySizeMiB
            : (int)Math.Ceiling(winreFileBytes / (1024.0 * 1024.0)) + HeadroomMiB;
        return Math.Max(InstallDiskLayoutBuilder.RecoverySizeMiB, computed);
    }
}

public sealed record ParallelWindowsEntry(
    char DriveLetter,
    string WindowsDirectory,
    string Label);

public static class ParallelWindowsPlanner
{
    public static IReadOnlyList<string> BuildBcdBootArguments(
        ParallelWindowsEntry entry,
        char systemLetter,
        FirmwareMode firmware) =>
    [
        entry.WindowsDirectory,
        "/s",
        $"{char.ToUpperInvariant(systemLetter)}:",
        "/f",
        firmware == FirmwareMode.UefiGpt ? "UEFI" : "BIOS",
        "/d"
    ];
}

public static class ExtraWinPePlanner
{
    public static bool IsSupportedWimExtension(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path.Trim()), ".wim", StringComparison.OrdinalIgnoreCase);

    public static (char DriveLetter, string RelativePath) ResolveRelativePath(string fullWimPath)
    {
        if (string.IsNullOrWhiteSpace(fullWimPath))
        {
            throw new ArgumentException("WIM path cannot be empty.", nameof(fullWimPath));
        }

        var trimmed = fullWimPath.Trim();
        if (trimmed.Length < 4 || !char.IsLetter(trimmed[0]) || trimmed[1] != ':' || (trimmed[2] != '\\' && trimmed[2] != '/'))
        {
            throw new ArgumentException($"Extra WinPE path must use a local drive letter root: {fullWimPath}", nameof(fullWimPath));
        }

        var driveLetter = char.ToUpperInvariant(trimmed[0]);
        var rest = trimmed[2..].Replace('/', '\\');
        if (!rest.StartsWith('\\'))
        {
            rest = "\\" + rest;
        }

        return (driveLetter, rest);
    }

    public static string? ParseCreatedBcdGuid(string bcdeditCreateOutput)
    {
        if (string.IsNullOrWhiteSpace(bcdeditCreateOutput))
        {
            return null;
        }

        var start = 0;
        while (start < bcdeditCreateOutput.Length)
        {
            var open = bcdeditCreateOutput.IndexOf('{', start);
            if (open < 0)
            {
                break;
            }

            var close = bcdeditCreateOutput.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            var inner = bcdeditCreateOutput[(open + 1)..close];
            if (Guid.TryParse(inner, out var parsed))
            {
                return "{" + parsed.ToString("D") + "}";
            }

            start = close + 1;
        }

        return null;
    }

    public static IReadOnlyList<IReadOnlyList<string>> BuildRamdiskEntryCommands(
        string bcdStorePath,
        string entryGuid,
        char wimDriveLetter,
        string wimRelativePath,
        FirmwareMode firmware,
        bool setTimeoutTenSeconds = true)
    {
        var drive = char.ToUpperInvariant(wimDriveLetter);
        var rel = wimRelativePath.StartsWith('\\') ? wimRelativePath : "\\" + wimRelativePath;
        var ramdiskSpec = $"ramdisk=[{drive}:]{rel},{{ramdiskoptions}}";
        var loaderPath = firmware == FirmwareMode.UefiGpt
            ? @"\Windows\system32\boot\winload.efi"
            : @"\Windows\system32\boot\winload.exe";

        var commands = new List<IReadOnlyList<string>>
        {
            new[] { "/store", bcdStorePath, "/set", "{ramdiskoptions}", "ramdisksdidevice", "boot" },
            new[] { "/store", bcdStorePath, "/set", "{ramdiskoptions}", "ramdisksdipath", @"\Boot\boot.sdi" },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "device", ramdiskSpec },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "osdevice", ramdiskSpec },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "path", loaderPath },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "systemroot", @"\Windows" },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "detecthal", "yes" },
            new[] { "/store", bcdStorePath, "/set", entryGuid, "winpe", "yes" },
            new[] { "/store", bcdStorePath, "/displayorder", entryGuid, "/addlast" }
        };

        if (setTimeoutTenSeconds)
        {
            commands.Add(["/store", bcdStorePath, "/timeout", "10"]);
        }

        return commands;
    }
}

public static class WinPeImageClassifier
{
    public static bool LooksLikeWinPe(string dismWimInfoOutput, string fileName)
    {
        if (string.IsNullOrWhiteSpace(dismWimInfoOutput))
        {
            return false;
        }

        var hasPeMarker = dismWimInfoOutput.Contains("WindowsPE", StringComparison.OrdinalIgnoreCase)
            || dismWimInfoOutput.Contains("Microsoft Windows PE", StringComparison.OrdinalIgnoreCase)
            || dismWimInfoOutput.Contains("Windows Recovery Environment", StringComparison.OrdinalIgnoreCase)
            || dismWimInfoOutput.Contains("FelixPE", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(Path.GetFileName(fileName), "install.wim", StringComparison.OrdinalIgnoreCase))
        {
            return dismWimInfoOutput.Contains("WindowsPE", StringComparison.OrdinalIgnoreCase);
        }

        return hasPeMarker;
    }
}
