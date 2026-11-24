namespace Felix.WindowSetup.Core;

public enum WindowsBackupMethod
{
    LiveVssOrOfflineImage,
    WinPeOfflineCapture,
    SysprepGeneralizeCapture
}

public sealed record BackupSourceInstallation(
    string RootPath,
    string EditionName,
    string VersionLabel,
    bool IsCurrentRunningWindows,
    int DiskNumber = -1,
    int PartitionNumber = 0,
    bool HasBitLocker = false)
{
    public string DisplayLabel
    {
        get
        {
            var runningSuffix = IsCurrentRunningWindows ? " [Current Windows]" : string.Empty;
            var versionPart = string.IsNullOrWhiteSpace(VersionLabel) ? string.Empty : $" ({VersionLabel})";
            return $"{RootPath.TrimEnd('\\')} — {EditionName}{versionPart}{runningSuffix}";
        }
    }
}

public sealed record SysprepBackupOptions(
    bool Generalize = true,
    bool EnterAuditMode = false,
    bool StripThirdPartyDrivers = false,
    bool StripStorageDrivers = false,
    bool AutoRestoreSafetyBackupOnSuccess = true,
    bool AutoRestoreSafetyBackupOnFailure = true,
    int FailureCountdownSeconds = 60,
    string? UnattendXmlPath = null);

public sealed record WindowsBackupRequest(
    BackupSourceInstallation Source,
    WindowsBackupMethod Method,
    BackupArchiveFormat Format,
    string OutputPath,
    string ImageName,
    string? ImageDescription = null,
    bool MakePortableAcrossHardware = false,
    bool ShutdownAfterCapture = false,
    int SplitPartSizeMiB = 3800,
    SysprepBackupOptions? Sysprep = null);

public sealed record WindowsBackupPreflight(
    bool IsReady,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed class WindowsBackupSession
{
    public int Version { get; set; } = 1;
    public string SessionId { get; set; } = "";
    public string Stage { get; set; } = "Prepared";
    public WindowsBackupMethod Method { get; set; }
    public BackupArchiveFormat Format { get; set; }
    public string SourceRootHint { get; set; } = "";
    public int SourceDiskNumber { get; set; } = -1;
    public int SourcePartitionNumber { get; set; }
    public string OutputPath { get; set; } = "";
    public string? FinalSwmFirstPartPath { get; set; }
    public int SplitPartSizeMiB { get; set; } = 3800;
    public string ImageName { get; set; } = "";
    public string? ImageDescription { get; set; }
    public bool MakePortableAcrossHardware { get; set; }
    public bool ShutdownAfterCapture { get; set; }
    public SysprepBackupOptions? Sysprep { get; set; }
    public string? SafetyBackupWimPath { get; set; }
    public bool CreatedSafetyBackup { get; set; }
    public bool CreatedFelixWork { get; set; }
    public long FelixWorkOffset { get; set; }
    public string? StagingDriveLetter { get; set; }
    public string? StagingRelativeDir { get; set; }
    public string? WindowsBootEntryId { get; set; }
    public string? RamdiskDeviceId { get; set; }
    public string? WinPeWimRelativePath { get; set; }
    public string? WinPeSdiRelativePath { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class WindowsBackupMetadata
{
    public const string InPlaceEditionMarker = "Felix Backup - InPlace";
    public const string SysprepEditionMarker = "Felix Backup - Sysprep";

    public static bool IsInPlaceBackupEdition(string? edition)
    {
        if (string.IsNullOrWhiteSpace(edition))
        {
            return false;
        }

        if (edition.Contains(InPlaceEditionMarker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return edition.Contains("InPlace", StringComparison.OrdinalIgnoreCase)
            && (edition.Contains("Backup", StringComparison.OrdinalIgnoreCase)
                || edition.Contains("Felix", StringComparison.OrdinalIgnoreCase));
    }

    public static string EnsureImageNameMarker(string imageName, WindowsBackupMethod method)
    {
        var trimmed = string.IsNullOrWhiteSpace(imageName) ? "Windows Backup" : imageName.Trim();
        var marker = method == WindowsBackupMethod.SysprepGeneralizeCapture
            ? SysprepEditionMarker
            : InPlaceEditionMarker;

        if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed} ({marker})";
    }

    public static string NormalizeOutputPath(string outputPath, BackupArchiveFormat format)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return string.Empty;
        }

        var trimmed = outputPath.Trim().Trim('"');
        var desiredExtension = format switch
        {
            BackupArchiveFormat.Esd => ".esd",
            BackupArchiveFormat.Swm => ".swm",
            _ => ".wim"
        };

        if (string.Equals(Path.GetExtension(trimmed), desiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return Path.ChangeExtension(trimmed, desiredExtension);
    }

    public static string CreateCaptureExclusionConfig() =>
        string.Join(
            "\r\n",
            [
                "[ExclusionList]",
                @"\hiberfil.sys",
                @"\pagefile.sys",
                @"\swapfile.sys",
                @"\System Volume Information",
                @"\$Recycle.Bin",
                @"\$WINDOWS.~BT",
                @"\$WINDOWS.~WS",
                @"\$FelixWinDeploy",
                @"\Windows\CSC",
                @"\Windows\Temp\*",
                @"\Recovery"
            ]);
}

public static class WindowsBackupPreflightValidator
{
    public static WindowsBackupPreflight Validate(
        WindowsBackupRequest request,
        bool isRunningInWinPe,
        bool isRebootPending = false,
        bool isInAuditMode = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (request.Source is null || string.IsNullOrWhiteSpace(request.Source.RootPath))
        {
            errors.Add("Select a Windows installation to back up.");
        }
        else if (request.Source.HasBitLocker)
        {
            errors.Add("Unlock or disable BitLocker on the source volume before backing it up.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath) || !Path.IsPathRooted(request.OutputPath))
        {
            errors.Add("Choose a valid destination file path on a local or external drive.");
        }
        else
        {
            var fullOutput = Path.GetFullPath(request.OutputPath);
            if (fullOutput.StartsWith(@"X:\", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Cannot save the backup archive onto the temporary WinPE RAM drive (X:).");
            }

            if (request.Source is not null && !string.IsNullOrWhiteSpace(request.Source.RootPath))
            {
                var sourceWin = Path.GetFullPath(Path.Combine(request.Source.RootPath, "Windows"))
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullOutput.StartsWith(sourceWin, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Cannot save the backup archive inside the Windows folder being captured.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(request.ImageName))
        {
            errors.Add("Provide an image name for the backup archive.");
        }

        if (request.Format == BackupArchiveFormat.Swm && request.SplitPartSizeMiB < 100)
        {
            errors.Add("SWM split size must be at least 100 MiB.");
        }

        if (request.Method == WindowsBackupMethod.SysprepGeneralizeCapture)
        {
            if (isRunningInWinPe)
            {
                errors.Add("Sysprep capture must be initiated from a running Windows desktop installation.");
            }
            else if (request.Source is not null && !request.Source.IsCurrentRunningWindows)
            {
                errors.Add("Method C (Sysprep) can only target the currently running Windows installation.");
            }

            if (isRebootPending)
            {
                errors.Add("A Windows restart is pending. Reboot Windows first before running Sysprep.");
            }

            if (isInAuditMode && request.Sysprep?.EnterAuditMode == true)
            {
                warnings.Add("Windows is already running in Audit mode.");
            }

            if (request.Sysprep?.StripStorageDrivers == true)
            {
                warnings.Add("Stripping 3rd-party storage drivers can prevent booting on specialized RAID/NVMe controllers unless generic drivers apply.");
            }
        }
        else if (request.Method == WindowsBackupMethod.WinPeOfflineCapture && isRunningInWinPe)
        {
            warnings.Add("Already running in WinPE; Method B will capture the selected volume offline directly without rebooting.");
        }

        return new WindowsBackupPreflight(errors.Count == 0, errors, warnings);
    }
}
