using System.Globalization;

namespace Felix.WindowSetup.Core;

public sealed record InstallDiskLayout(
    string Script,
    char SystemLetter,
    char WindowsLetter,
    char? RecoveryLetter);

public static class InstallDiskLayoutBuilder
{
    public const int EfiSizeMiB = 260;
    public const int BiosSystemSizeMiB = 550;
    public const int MsrSizeMiB = 16;
    public const int RecoverySizeMiB = 1024;
    public const long MinimumDiskBytes = 32L * 1024 * 1024 * 1024;

    public static InstallDiskLayout Create(
        int diskNumber,
        FirmwareMode firmware,
        char systemLetter,
        char windowsLetter,
        char? recoveryLetter,
        bool createRecovery)
    {
        ValidateDiskNumber(diskNumber);
        ValidateLetter(systemLetter);
        ValidateLetter(windowsLetter);
        if (recoveryLetter is char recovery)
        {
            ValidateLetter(recovery);
        }
        var letters = new[] { systemLetter, windowsLetter }
            .Concat(recoveryLetter is char value ? [value] : [])
            .ToArray();
        if (letters.Distinct().Count() != letters.Length)
        {
            throw new InvalidOperationException("Installation access paths must use different drive letters.");
        }
        if (createRecovery && recoveryLetter is null)
        {
            throw new InvalidOperationException("A Recovery access path is required.");
        }

        var disk = diskNumber.ToString(CultureInfo.InvariantCulture);
        var script = firmware == FirmwareMode.UefiGpt
            ? GptScript(disk, systemLetter, windowsLetter, recoveryLetter, createRecovery)
            : MbrScript(disk, systemLetter, windowsLetter, recoveryLetter, createRecovery);
        return new InstallDiskLayout(script, systemLetter, windowsLetter, createRecovery ? recoveryLetter : null);
    }

    public static string CreateShrinkRecoveryScript(
        int diskNumber,
        int windowsPartitionNumber,
        FirmwareMode firmware,
        int recoverySizeMiB,
        char recoveryLetter)
    {
        ValidateDiskNumber(diskNumber);
        if (windowsPartitionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowsPartitionNumber));
        }
        if (recoverySizeMiB < RecoverySizeMiB)
        {
            throw new ArgumentOutOfRangeException(nameof(recoverySizeMiB));
        }
        ValidateLetter(recoveryLetter);

        var lines = new List<string>
        {
            $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}",
            $"select partition {windowsPartitionNumber.ToString(CultureInfo.InvariantCulture)}",
            $"shrink desired={recoverySizeMiB.ToString(CultureInfo.InvariantCulture)} minimum={recoverySizeMiB.ToString(CultureInfo.InvariantCulture)}",
            "create partition primary"
        };
        if (firmware == FirmwareMode.UefiGpt)
        {
            lines.Add("set id=DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
            lines.Add("gpt attributes=0x8000000000000001");
        }
        else
        {
            lines.Add("set id=27 override");
        }
        lines.Add("format quick fs=ntfs label=Recovery");
        lines.Add($"assign letter={char.ToUpperInvariant(recoveryLetter)}");
        return string.Join('\n', lines) + "\n";
    }

    private static string GptScript(
        string disk,
        char system,
        char windows,
        char? recovery,
        bool createRecovery)
    {
        var lines = new List<string>
        {
            $"select disk {disk}",
            "clean",
            "convert gpt",
            $"create partition efi size={EfiSizeMiB}",
            "format quick fs=fat32 label=System",
            $"assign letter={system}",
            $"create partition msr size={MsrSizeMiB}",
            "create partition primary",
            "format quick fs=ntfs label=Windows",
            $"assign letter={windows}"
        };
        AddRecovery(lines, recovery, createRecovery, gpt: true);
        return string.Join('\n', lines) + "\n";
    }

    private static string MbrScript(
        string disk,
        char system,
        char windows,
        char? recovery,
        bool createRecovery)
    {
        var lines = new List<string>
        {
            $"select disk {disk}",
            "clean",
            "convert mbr",
            $"create partition primary size={BiosSystemSizeMiB}",
            "format quick fs=ntfs label=System",
            "active",
            $"assign letter={system}",
            "create partition primary",
            "format quick fs=ntfs label=Windows",
            $"assign letter={windows}"
        };
        AddRecovery(lines, recovery, createRecovery, gpt: false);
        return string.Join('\n', lines) + "\n";
    }

    private static void AddRecovery(
        ICollection<string> lines,
        char? recovery,
        bool createRecovery,
        bool gpt)
    {
        if (!createRecovery)
        {
            return;
        }
        lines.Add($"shrink desired={RecoverySizeMiB} minimum={RecoverySizeMiB}");
        lines.Add("create partition primary");
        if (gpt)
        {
            lines.Add("set id=DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
            lines.Add("gpt attributes=0x8000000000000001");
        }
        else
        {
            lines.Add("set id=27 override");
        }
        lines.Add("format quick fs=ntfs label=Recovery");
        lines.Add($"assign letter={recovery}");
    }

    public static string CreateFelixWorkShrinkScript(
        int diskNumber,
        int windowsPartitionNumber,
        int sizeMiB,
        char workLetter)
    {
        ValidateDiskNumber(diskNumber);
        if (windowsPartitionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowsPartitionNumber));
        }
        if (sizeMiB < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeMiB));
        }
        ValidateLetter(workLetter);

        var lines = new[]
        {
            $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}",
            $"select partition {windowsPartitionNumber.ToString(CultureInfo.InvariantCulture)}",
            $"shrink desired={sizeMiB.ToString(CultureInfo.InvariantCulture)} minimum={sizeMiB.ToString(CultureInfo.InvariantCulture)}",
            "create partition primary",
            "format quick fs=ntfs label=FELIXWORK",
            $"assign letter={char.ToUpperInvariant(workLetter)}"
        };
        return string.Join('\n', lines) + "\n";
    }

    public static string CreateRemoveFelixWorkAndExtendScript(
        int diskNumber,
        int felixWorkPartitionNumber,
        int windowsPartitionNumber)
    {
        ValidateDiskNumber(diskNumber);
        if (felixWorkPartitionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(felixWorkPartitionNumber));
        }
        if (windowsPartitionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowsPartitionNumber));
        }

        var lines = new[]
        {
            $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}",
            $"select partition {felixWorkPartitionNumber.ToString(CultureInfo.InvariantCulture)}",
            "delete partition override",
            $"select partition {windowsPartitionNumber.ToString(CultureInfo.InvariantCulture)}",
            "extend"
        };
        return string.Join('\n', lines) + "\n";
    }

    private static void ValidateDiskNumber(int diskNumber)
    {
        if (diskNumber is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(diskNumber));
        }
    }

    private static void ValidateLetter(char letter)
    {
        if (char.ToUpperInvariant(letter) is < 'D' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(letter));
        }
    }
}
