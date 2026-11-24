using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.ViewModels;

public sealed class ReviewViewModel : ObservableObject
{
    private string typedDiskNumber = "";

    public ReviewViewModel(
        WindowSetupRequest request,
        TargetPlanSummary summary,
        SetupExecutionMode executionMode = SetupExecutionMode.DirectWinPe,
        RescueUsbCandidate? rescueUsb = null)
    {
        Request = request;
        Summary = summary;
        var isErase = request.Target.Mode == InstallTargetMode.EraseDisk;
        var keptNames = summary.Kept
            .Select(partition => string.IsNullOrWhiteSpace(partition.Letter)
                ? (string.IsNullOrWhiteSpace(partition.Label) ? $"Partition {partition.Number}" : partition.Label)
                : $"{partition.Letter}: {partition.Label}".Trim())
            .ToArray();
        var firmwareText = request.Target.Firmware == FirmwareMode.UefiGpt ? "UEFI / GPT" : "BIOS / MBR";
        var options = request.Options ?? new InstallOptions();
        var enabledOptions = new List<string>();
        if (executionMode == SetupExecutionMode.DesktopHandoff) enabledOptions.Add("Prepare & reboot into FelixPE");
        if (options.VerifyImage) enabledOptions.Add("Verify image");
        if (options.BypassMicrosoftAccount) enabledOptions.Add("No Microsoft account");
        if (options.ClearBootMenu) enabledOptions.Add("Clear boot menu");
        if (options.RestartWhenComplete) enabledOptions.Add("Restart when complete");
        if (options.Drivers is { } drivers)
        {
            if (drivers.SelectedDriverPublishedNames is { Count: > 0 } selected)
            {
                enabledOptions.Add($"{selected.Count} drivers");
            }
            else if (drivers.BackupPreset != DriverBackupPreset.None)
            {
                enabledOptions.Add($"Drivers ({drivers.BackupPreset})");
            }

            if (drivers.ExportConnectedWifi) enabledOptions.Add("Export Wi-Fi");
            if (drivers.BackupPrinters) enabledOptions.Add("Backup printers");
            if (!string.IsNullOrWhiteSpace(drivers.WindowsDriversFolder)) enabledOptions.Add("Windows drivers");
            if (!string.IsNullOrWhiteSpace(drivers.WinPeDriversFolder)) enabledOptions.Add("WinPE drivers");
        }
        if (options.BootAndRecovery is { } boot)
        {
            if (!isErase
                && string.Equals(request.Target.RecoveryPartition.Identity, "none", StringComparison.OrdinalIgnoreCase)
                && options.CreateRecovery
                && boot.CreateNewRecovery)
            {
                enabledOptions.Add("Create Recovery");
            }
            if (boot.AddParallelWindows) enabledOptions.Add("Parallel Windows");
            if (!string.IsNullOrWhiteSpace(boot.ExtraWinPePath)) enabledOptions.Add("Extra WinPE");
        }
        if (options.FirstBoot is { } firstBoot)
        {
            var enabledRemote = (firstBoot.RemoteApps ?? []).Where(a => a.Enabled)
                .Select(a => RemoteSupportCatalog.DisplayName(a.Kind))
                .ToArray();
            if (enabledRemote.Length > 0)
            {
                enabledOptions.Add(string.Join(", ", enabledRemote));
            }
            if (firstBoot.HasFirstDesktopApp)
            {
                enabledOptions.Add(Path.GetFileName(firstBoot.FirstDesktopApp!.SourcePath)!);
            }
            if (firstBoot.WriteDesktopSummaryReport)
            {
                enabledOptions.Add("Desktop report");
            }
        }
        if (options.Bloatware is { Enabled: true })
        {
            enabledOptions.Add("Remove bloatware");
        }
        if (options.Recording is { IsActive: true })
        {
            enabledOptions.Add($"Record ({options.Recording.Mode})");
        }
        if (rescueUsb is { DiskNumber: >= 0 })
        {
            enabledOptions.Add($"Rescue USB (Disk {rescueUsb.DiskNumber})");
        }
        var optionsText = enabledOptions.Count == 0 ? "Default" : string.Join(", ", enabledOptions);
        var recoveryValue = isErase
            ? (options.CreateRecovery ? "Create automatically" : "Do not use")
            : (!string.Equals(request.Target.RecoveryPartition.Identity, "none", StringComparison.OrdinalIgnoreCase)
                ? request.Target.RecoveryPartition.DisplayName
                : (options.CreateRecovery && options.BootAndRecovery?.CreateNewRecovery == true
                    ? "Create by shrinking Windows"
                    : request.Target.RecoveryPartition.DisplayName));

        Rows =
        [
            ("WS_Review_Windows", request.Source.Edition),
            ("WS_Review_Source", Path.GetFileName(request.Source.ImagePath)),
            ("WS_Review_Disk", $"Disk {request.Target.DiskNumber} · {request.Target.DiskModel} · {request.Target.DiskBus}".Trim(' ', '·')),
            ("WS_Review_WindowsPartition", isErase ? $"Create automatically ({firmwareText})" : request.Target.WindowsPartition.DisplayName),
            ("WS_Review_SystemPartition", isErase ? $"Create automatically ({firmwareText})" : request.Target.EfiSystemPartition.DisplayName),
            ("WS_Review_RecoveryPartition", recoveryValue),
            ("WS_Review_Keep", keptNames.Length == 0 ? "—" : string.Join(", ", keptNames)),
            ("WS_Review_Setup", !string.IsNullOrWhiteSpace(request.AutounattendPath)
                ? Path.GetFileName(request.AutounattendPath)
                : (options.GeneratedUnattend is not null ? "Auto Unattend" : "Normal Windows setup")),
            ("WS_Review_Options", optionsText)
        ];
        var usbFormatRequired = rescueUsb is { DiskNumber: >= 0, PreparationMode: RescueUsbMode.FormatDisk };
        RequiresDiskNumber = isErase || usbFormatRequired;
        ExpectedConfirmation = isErase
            ? request.Target.DiskNumber.ToString()
            : (usbFormatRequired ? rescueUsb!.ConfirmationToken : "");
        WarningKey = isErase ? "WS_Review_EraseWarning" : "WS_Review_FormatWarning";
        var baseWarning = isErase
            ? $"Disk {request.Target.DiskNumber} ({request.Target.DiskModel}) and all its partitions will be erased."
            : $"The selected Windows partition ({request.Target.WindowsPartition.DisplayName}) will be formatted.";
        Warning = usbFormatRequired
            ? $"{baseWarning} Rescue USB Disk {rescueUsb!.DiskNumber} will also be erased (type {ExpectedConfirmation})."
            : baseWarning;
    }

    public WindowSetupRequest Request { get; }
    public TargetPlanSummary Summary { get; }
    public IReadOnlyList<(string LabelKey, string Value)> Rows { get; }
    public bool RequiresDiskNumber { get; }
    public string ExpectedConfirmation { get; }
    public string WarningKey { get; }
    public string Warning { get; }

    public string TypedDiskNumber
    {
        get => typedDiskNumber;
        set
        {
            if (SetField(ref typedDiskNumber, value))
            {
                Raise(nameof(CanConfirm));
            }
        }
    }

    public bool CanConfirm => !RequiresDiskNumber ||
        TypedDiskNumber.Trim().Equals(ExpectedConfirmation, StringComparison.OrdinalIgnoreCase);
}
