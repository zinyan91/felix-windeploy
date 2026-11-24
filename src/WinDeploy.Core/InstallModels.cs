namespace Felix.WindowSetup.Core;

public enum InstallTargetMode
{
    ExistingPartition,
    EraseDisk
}

public enum FirmwareMode
{
    UefiGpt,
    BiosMbr
}

public sealed record WindowsSource(
    string ImagePath,
    int ImageIndex,
    string Edition,
    string Architecture = "",
    string Version = "");

public sealed record PartitionTarget(
    string Identity,
    string DisplayName,
    string AccessPath = "",
    int Number = 0,
    long Offset = 0,
    long Size = 0,
    string Guid = "",
    string VolumeSerial = "",
    string FileSystem = "",
    bool IsBitLocker = false);

public sealed record InstallTarget(
    InstallTargetMode Mode,
    string DiskIdentity,
    FirmwareMode Firmware,
    PartitionTarget WindowsPartition,
    PartitionTarget EfiSystemPartition,
    PartitionTarget RecoveryPartition,
    int DiskNumber = -1,
    string DiskId = "",
    string DiskSerial = "",
    long DiskSize = 0,
    string DiskStyle = "",
    bool HoldsPeMedia = false,
    bool IsReadOnly = false,
    bool IsOffline = false,
    bool IsDynamic = false,
    string DiskModel = "",
    string DiskBus = "",
    bool DiskHasExistingRecovery = false);

public sealed record BootAndRecoveryOptions(
    bool CreateNewRecovery = true,
    bool AddParallelWindows = false,
    string? ExtraWinPePath = null,
    string? ExtraWinPeTitle = null);

public sealed record DriverAndNetworkOptions(
    DriverBackupPreset BackupPreset = DriverBackupPreset.None,
    IReadOnlyList<string>? SelectedDriverPublishedNames = null,
    bool ExportConnectedWifi = false,
    bool BackupPrinters = false,
    string? WindowsDriversFolder = null,
    string? WinPeDriversFolder = null)
{
    public bool HasBackupOrStagingWork =>
        BackupPreset != DriverBackupPreset.None
        || (SelectedDriverPublishedNames is { Count: > 0 })
        || ExportConnectedWifi
        || BackupPrinters
        || !string.IsNullOrWhiteSpace(WinPeDriversFolder);

    public bool HasInjectionOrFirstBootWork =>
        BackupPreset != DriverBackupPreset.None
        || (SelectedDriverPublishedNames is { Count: > 0 })
        || ExportConnectedWifi
        || BackupPrinters
        || !string.IsNullOrWhiteSpace(WindowsDriversFolder);
}

public sealed record InstallOptions(
    bool VerifyImage = false,
    bool CreateRecovery = true,
    bool ClearBootMenu = false,
    bool RestartWhenComplete = false,
    bool BypassMicrosoftAccount = true,
    DriverAndNetworkOptions? Drivers = null,
    BootAndRecoveryOptions? BootAndRecovery = null,
    FirstBootOptions? FirstBoot = null,
    BloatwareOptions? Bloatware = null,
    UnattendSpec? GeneratedUnattend = null,
    WinPeRecordingOptions? Recording = null,
    int? RescueUsbDiskNumber = null);

public sealed record WindowSetupRequest(
    WindowsSource Source,
    InstallTarget Target,
    string? AutounattendPath,
    InputFileInspection? FileInspection = null,
    InstallOptions? Options = null);

public sealed record PreflightIssue(
    string Code,
    string Message,
    bool IsBlocking);

public sealed record PreflightResult(
    IReadOnlyList<PreflightIssue> Issues)
{
    public bool CanProceed => Issues.All(issue => !issue.IsBlocking);
}
