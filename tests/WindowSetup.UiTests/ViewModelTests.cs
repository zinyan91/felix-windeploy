using Felix.Partition.Services;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.UiTests;

internal static class ViewModelTests
{
    public static async Task RunAsync()
    {
        var targets = new FakeTargetSource([GptWindowsDisk(0), UsbDataDisk(1, "Q")]);
        var inspector = new FakeImageInspector();
        var scanner = new FakeSourceScanner();
        var vm = new WindowSetupViewModel(targets, inspector, scanner, FirmwareMode.UefiGpt, isWinPe: true);

        await vm.Target.RefreshAsync();
        await vm.Source.SelectImageAsync(@"Q:\Win11.iso");
        Assert(vm.Target.Disks[1].Block == DiskBlockReason.HoldsSetupFiles, "Image disk must be greyed out.");

        var winCard = vm.Target.Disks[0].Cards.Single(c => c.Facts.Label == "Windows");
        vm.Target.SelectPartition(winCard);
        Assert(vm.Target.Roles is { System.Label: "EFI" } && vm.Target.IsValid, "Roles should be suggested.");
        Assert(vm.CanStart, "CanStart should be true when source and target are valid.");

        await vm.Source.SelectImageAsync(@"M:\other.iso");
        Assert(vm.Target.Roles is null, "Selection on Disk 0 must drop when image moves to Disk 0 (M:).");
        Assert(vm.Target.Disks[0].Block == DiskBlockReason.HoldsSetupFiles, "Disk 0 must be blocked.");
        Assert(vm.Target.Disks[1].Block == DiskBlockReason.None, "Disk 1 must unblock.");

        await vm.Source.SelectImageAsync(@"Q:\Win11.iso");
        vm.Target.SelectPartition(vm.Target.Disks[0].Cards.Single(c => c.Facts.Label == "Windows"));
        Assert(vm.Target.Roles is not null, "Roles selected before refresh.");

        targets.Next = [GptWindowsDisk(0, windowsOffset: 2L << 30), UsbDataDisk(1, "Q")];
        await vm.Target.RefreshAsync();
        Assert(vm.Target.Roles is null, "Stale partition selection must drop when offset shifts on refresh.");

        var mbrOnly = new TargetDiskFacts(2, "id2", "s2", "MBR Disk", "SATA", "MBR", 64L << 30,
            false, false, false, false, false, true,
            [new TargetPartitionFacts(1, 1024 * 1024, 60L << 30, "G", "Data", "NTFS", "", "1234", false, false, false, false, false, false, false)]);
        targets.Next = [GptWindowsDisk(0), mbrOnly];
        await vm.Target.RefreshAsync();
        Assert(vm.Target.Disks[1].PartitionBlock == PartitionModeBlock.StyleMismatch, "MBR under UEFI should block partition mode.");
        Assert(TargetRules.CanErase(vm.Target.Disks[1].Facts), "MBR under UEFI should still allow erase mode.");

        vm.Target.Mode = InstallTargetMode.EraseDisk;
        vm.Target.SelectDiskForErase(vm.Target.Disks[1]);
        Assert(vm.Target.Summary is { Lines.Count: 4 } && vm.Target.Summary.Lost.Count == 1, "Erase preview must show 4 planned partitions.");

        var req = vm.CreateRequest();
        var review = new ReviewViewModel(req, vm.Target.Summary!);
        Assert(review.RequiresDiskNumber && !review.CanConfirm, "Erase review requires disk number.");
        review.TypedDiskNumber = "0";
        Assert(!review.CanConfirm, "Wrong disk number must not enable confirm.");
        review.TypedDiskNumber = " 2 ";
        Assert(review.CanConfirm, "Matching disk number must enable confirm.");
        Assert(review.Rows.All(r => !r.Value.Contains('|')), "Review rows must never leak internal pipe-delimited identities.");

        var keepBootPlan = new WindowsBootRepairPlan('W', 'S', "UEFI", ClearBootMenu: false);
        Assert(!keepBootPlan.Commands()[0].Arguments.Contains("/c"), "ClearBootMenu=false must not pass /c to bcdboot.");
        var clearBootPlan = new WindowsBootRepairPlan('W', 'S', "UEFI", ClearBootMenu: true);
        Assert(clearBootPlan.Commands()[0].Arguments.Contains("/c"), "ClearBootMenu=true must pass /c to bcdboot.");

        // CPU without POPCNT must block build 26100 in CanStart
        var weakCpuVm = new WindowSetupViewModel(targets, inspector, scanner, FirmwareMode.UefiGpt, isWinPe: true,
            cpu: new CpuCapabilities(HasPopcnt: false, HasSse42: true));
        await weakCpuVm.Target.RefreshAsync();
        await weakCpuVm.Source.SelectImageAsync(@"Q:\Win11.iso");
        weakCpuVm.Target.SelectPartition(weakCpuVm.Target.Disks[0].Cards.Single(c => c.Facts.Label == "Windows"));
        Assert(!weakCpuVm.CanStart && weakCpuVm.HasSourceCompatibilityError, "Weak CPU must block build 26100 in ViewModel.");

        // Options propagation
        vm.VerifyImage = true;
        vm.BypassMicrosoftAccount = false;
        vm.ClearBootMenu = true;
        vm.RestartWhenComplete = true;
        vm.ExportConnectedWifi = true;
        vm.WindowsDriversFolder = @"M:\WinDrivers";
        vm.WinPeDriversFolder = @"M:\PeDrivers";
        vm.ApplyDriverSelection(DriverBackupPreset.Minimum, ["oem1.inf", "oem2.inf"]);
        var optReq = vm.CreateRequest(inspectFiles: false);
        Assert(optReq.Options is { VerifyImage: true, BypassMicrosoftAccount: false, ClearBootMenu: true, RestartWhenComplete: true },
            "ViewModel options must propagate to WindowSetupRequest.");
        Assert(optReq.Options?.Drivers is
               {
                   BackupPreset: DriverBackupPreset.Minimum,
                   ExportConnectedWifi: true,
                   WindowsDriversFolder: @"M:\WinDrivers",
                   WinPeDriversFolder: @"M:\PeDrivers",
                   SelectedDriverPublishedNames.Count: 2
               },
            "Driver and network options must propagate to WindowSetupRequest.");

        // DriverSelectionViewModel preset & search
        var fakeDriverStore = new FakeDriverStoreService();
        var driverVm = new DriverSelectionViewModel(fakeDriverStore, DriverBackupPreset.Minimum);
        await driverVm.LoadAsync();
        Assert(driverVm.TotalCount == 3 && driverVm.SelectedCount == 2,
            "Minimum preset must select storage + network drivers.");
        driverVm.ApplyPreset(DriverBackupPreset.Recommended);
        Assert(driverVm.SelectedCount == 2, "Recommended preset must exclude Display drivers.");
        driverVm.SearchText = "nvidia";
        Assert(driverVm.FilteredDrivers.Count == 1 && driverVm.FilteredDrivers[0].PublishedName == "oem3.inf",
            "Driver search filter must narrow Visible rows.");

        // Boot & Recovery options and CanCreateNewRecovery
        vm.Target.Mode = InstallTargetMode.ExistingPartition;
        vm.Target.SelectPartition(vm.Target.Disks[0].Cards.Single(c => c.Facts.Label == "Windows"));
        Assert(!vm.CanCreateNewRecovery,
            "Disk with existing Recovery partition must disable CreateNewRecovery when Recovery is already assigned or present.");
        vm.Target.SetRecoveryUnused();
        Assert(!vm.CanCreateNewRecovery,
            "Disk with an existing Recovery partition must still block creating a duplicate Recovery partition.");

        var noRecTargets = new FakeTargetSource([GptDiskWithoutRecovery(3), UsbDataDisk(1, "Q")]);
        var noRecVm = new WindowSetupViewModel(noRecTargets, inspector, scanner, FirmwareMode.UefiGpt, isWinPe: true);
        await noRecVm.Target.RefreshAsync();
        await noRecVm.Source.SelectImageAsync(@"Q:\Win11.iso");
        noRecVm.Target.SelectPartition(noRecVm.Target.Disks[0].Cards.Single(c => c.Facts.Label == "Windows"));
        Assert(noRecVm.CanCreateNewRecovery,
            "Disk without Recovery must allow CreateNewRecovery by shrinking Windows.");
        noRecVm.AddParallelWindows = true;
        noRecVm.ExtraWinPePath = @"N:\Rescue\boot.wim";
        var bootReq = noRecVm.CreateRequest(inspectFiles: false);
        Assert(bootReq.Options?.CreateRecovery == true
            && bootReq.Options.BootAndRecovery is
               {
                   CreateNewRecovery: true,
                   AddParallelWindows: true,
                   ExtraWinPePath: @"N:\Rescue\boot.wim"
               },
            "Boot and Recovery options must propagate to WindowSetupRequest.");

        noRecVm.ExtraWinPePath = @"M:\WinPE\boot.wim";
        Assert(!noRecVm.CanStart, "ExtraWinPe on target Windows partition must disable CanStart.");
        noRecVm.ExtraWinPePath = @"Q:\boot.wim";
        Assert(!noRecVm.CanStart, "ExtraWinPe on USB removable disk must disable CanStart.");

        // RemoteAppsViewModel & FirstBoot propagation
        var remoteVm = new RemoteAppsViewModel();
        remoteVm.Items[0].IsEnabled = true; // AnyDesk
        Assert(remoteVm.CanApply && !string.IsNullOrWhiteSpace(remoteVm.Items[0].Password),
            "Enabling AnyDesk must auto-generate a valid default password.");
        remoteVm.Items[0].Password = "123";
        Assert(!remoteVm.CanApply && !string.IsNullOrWhiteSpace(remoteVm.Items[0].ValidationError),
            "Invalid password must block Apply and expose ValidationError.");
        remoteVm.Items[0].RegeneratePassword();
        Assert(remoteVm.CanApply && remoteVm.Items[0].ValidationError is null,
            "RegeneratePassword must restore a valid password.");

        vm.ApplyRemoteApps(remoteVm.BuildSelections());
        vm.FirstDesktopMode = FirstDesktopAppMode.PortableExe;
        vm.FirstDesktopSourcePath = @"N:\Tools\FelixHelper.exe";
        vm.InstallAppsSilently = false;
        vm.WriteDesktopSummaryReport = true;
        var fbReq = vm.CreateRequest(inspectFiles: false);
        Assert(fbReq.Options?.FirstBoot is
               {
                   InstallSilently: false,
                   WriteDesktopSummaryReport: true,
                   HasEnabledRemoteApps: true,
                   FirstDesktopApp: { Mode: FirstDesktopAppMode.PortableExe, SourcePath: @"N:\Tools\FelixHelper.exe" }
               },
            "FirstBoot options must propagate to WindowSetupRequest.");

        // bcdboot /bootex fallback verification
        var bootTarget = new FakeBootTarget();
        await WindowsBootRepair.ApplyAsync(
            bootTarget,
            new WindowsBootRepairPlan('W', 'S', "UEFI", ClearBootMenu: false, PreferBootEx: true),
            CancellationToken.None);
        Assert(bootTarget.RanCommands.Count >= 2 &&
               bootTarget.RanCommands[0].Arguments.Contains("/bootex") &&
               !bootTarget.RanCommands[1].Arguments.Contains("/bootex"),
            "WindowsBootRepair must try /bootex first and fall back to standard bcdboot on failure.");

        // Desktop Handoff mode verification
        var desktopTargets = new FakeTargetSource([GptWindowsDisk(0) with { RunsWindows = true, IsVirtual = false }]);
        var desktopVm = new WindowSetupViewModel(
            desktopTargets, inspector, scanner, FirmwareMode.UefiGpt, isWinPe: false, allowDesktopHandoff: true);
        await desktopVm.Target.RefreshAsync();
        await desktopVm.Source.SelectImageAsync(@"N:\Win11.iso");
        Assert(desktopVm.IsDesktopHandoffMode, "IsDesktopHandoffMode must be true when running from desktop.");
        Assert(desktopVm.Target.Disks[0].Block == DiskBlockReason.None,
            "Desktop disk with M: running Windows and N: ISO must remain enabled in ExistingPartition mode.");

        var dataCard = desktopVm.Target.Disks[0].Cards.Single(c => c.Facts.Letter == "N");
        desktopVm.Target.SelectPartition(dataCard);
        Assert(desktopVm.Target.Roles is null, "Partition holding ISO must not be selectable as Windows target.");

        var desktopWinCard = desktopVm.Target.Disks[0].Cards.Single(c => c.Facts.Letter == "M");
        desktopVm.Target.SelectPartition(desktopWinCard);
        Assert(desktopVm.Target.Roles is not null && desktopVm.ExecutionMode == SetupExecutionMode.DesktopHandoff,
            "Selecting physical Windows partition on Desktop must yield SetupExecutionMode.DesktopHandoff.");

        var handoffReview = new ReviewViewModel(desktopVm.CreateRequest(inspectFiles: false), desktopVm.Target.Summary!, desktopVm.ExecutionMode);
        Assert(handoffReview.Rows.Any(r => r.Value.Contains("Prepare & reboot into FelixPE")),
            "Desktop handoff review must display execution mode.");

        desktopVm.Target.Mode = InstallTargetMode.EraseDisk;
        Assert(desktopVm.Target.Disks[0].Block == DiskBlockReason.RunningWindows,
            "Switching to EraseDisk on Desktop must block the running Windows disk.");

        // Role ComboBoxes + AutoSelect + Bloatware + Critical storage warning verification
        desktopVm.Target.Mode = InstallTargetMode.ExistingPartition;
        desktopVm.Target.TryAutoSelectDefaultTarget();
        Assert(desktopVm.Target.Roles is { Windows.Letter: "M" },
            "TryAutoSelectDefaultTarget must auto-select the Windows partition.");
        Assert(desktopVm.Target.WindowsRoleOptions.Count >= 1
            && desktopVm.Target.SelectedWindowsOption?.Partition?.Number == 2
            && desktopVm.Target.SelectedSystemOption?.Partition?.Number == 1,
            "Role combo options must stay synchronized with Target.Roles.");

        desktopVm.Target.SelectedRecoveryOption = desktopVm.Target.RecoveryRoleOptions.First(o => o.Partition is null);
        Assert(desktopVm.Target.Roles?.Recovery is null,
            "Selecting Do not use in RecoveryRoleOptions must clear Recovery role.");

        desktopVm.ApplyBloatwareSelection(true, true, ["Microsoft.BingNews", "Clipchamp.Clipchamp"]);
        var phase1Req = desktopVm.CreateRequest(inspectFiles: false);
        Assert(phase1Req.Options?.Bloatware is { Enabled: true, BlockPromotedApps: true, RemovePackagePrefixes.Count: 2 }
            && phase1Req.Options?.GeneratedUnattend is not null,
            "Desktop ViewModel must propagate BloatwareOptions and auto-generated UnattendSpec.");

        var driverWarnVm = new DriverSelectionViewModel(new FakeDriverStoreService(), DriverBackupPreset.Minimum);
        await driverWarnVm.LoadAsync();
        Assert(!driverWarnVm.HasMissingCriticalStorageWarning, "Minimum preset keeps Intel RST/VMD checked.");
        driverWarnVm.ClearSelection();
        Assert(driverWarnVm.HasMissingCriticalStorageWarning && driverWarnVm.MissingCriticalStorageWarning!.Contains("iaStorVD.inf"),
            "Unchecking iaStorVD must trigger MissingCriticalStorageWarning.");

        // Rescue USB & Recording propagation
        var usbVm = new WindowSetupViewModel(
            new FakeTargetSource([GptWindowsDisk(0), UsbDataDisk(1, "Q"), UsbDataDisk(2, "Z")]),
            inspector, scanner, FirmwareMode.UefiGpt, isWinPe: false, allowDesktopHandoff: true);
        await usbVm.Target.RefreshAsync();
        await usbVm.Source.SelectImageAsync(@"Q:\Win11.iso");
        usbVm.Target.TryAutoSelectDefaultTarget();
        Assert(usbVm.RescueUsbOptions.Any(u => u.DiskNumber == 2)
            && !usbVm.RescueUsbOptions.Any(u => u.DiskNumber == 1),
            "RescueUsbOptions must include eligible Disk 2 and exclude setup-file Disk 1 (Q:).");
        usbVm.SelectedRescueUsbOption = usbVm.RescueUsbOptions.First(u => u.DiskNumber == 2);
        usbVm.ApplyRecordingOptions(new WinPeRecordingOptions(WinPeRecordMode.Both, 30));
        var phase2Req = usbVm.CreateRequest(inspectFiles: false);
        Assert(phase2Req.Options?.Recording is { Mode: WinPeRecordMode.Both, ScreenshotIntervalSeconds: 30 }
            && phase2Req.Options?.RescueUsbDiskNumber == 2,
            "ViewModel must propagate RecordingOptions and RescueUsbDiskNumber into WindowSetupRequest.");

        // InPlace Backup Restore lockout + Backup & Help ViewModels
        var inPlaceVm = new WindowSetupViewModel(
            new FakeTargetSource([GptWindowsDisk(0)]),
            new InPlaceImageInspector(),
            scanner,
            FirmwareMode.UefiGpt,
            isWinPe: false,
            allowDesktopHandoff: true,
            backupService: new FakeWindowsBackupService());
        await inPlaceVm.Target.RefreshAsync();
        await inPlaceVm.Source.SelectImageAsync(@"N:\MyBackup.wim");
        inPlaceVm.Target.TryAutoSelectDefaultTarget();
        inPlaceVm.ApplyBloatwareSelection(true, true, ["Microsoft.BingNews"]);
        inPlaceVm.BypassMicrosoftAccount = true;
        Assert(inPlaceVm.IsInPlaceBackupSource && !inPlaceVm.IsInstallCustomizationAllowed,
            "InPlace backup source must enable IsInPlaceBackupSource and lock install customizations.");
        var restoreReq = inPlaceVm.CreateRequest(inspectFiles: false);
        Assert(restoreReq.Options is { BypassMicrosoftAccount: false, GeneratedUnattend: null, Bloatware.Enabled: false },
            "InPlace backup restore request must strip BypassMicrosoftAccount, GeneratedUnattend, and Bloatware.");

        await inPlaceVm.Backup.RefreshSourcesAsync();
        Assert(inPlaceVm.Backup.Sources.Count == 1 && inPlaceVm.Backup.SelectedSource?.IsCurrentRunningWindows == true,
            "WindowsBackupViewModel must discover and select running Windows installation.");
        inPlaceVm.Backup.OutputPath = @"D:\Backups\Win11.wim";
        inPlaceVm.Backup.Format = BackupArchiveFormat.Swm;
        Assert(inPlaceVm.Backup.OutputPath.EndsWith(".swm", StringComparison.OrdinalIgnoreCase) && inPlaceVm.Backup.IsSwmFormat,
            "Switching format to SWM must normalize extension to .swm.");
        inPlaceVm.Backup.Method = WindowsBackupMethod.SysprepGeneralizeCapture;
        Assert(inPlaceVm.Backup.IsSysprepMethod && !inPlaceVm.Backup.CanStartBackup,
            "Pending reboot in FakeWindowsBackupService must block Sysprep CanStartBackup.");

        var hwVm = new HardwareCheckViewModel(new FakeHardwareProfileService());
        await hwVm.RefreshAsync(isVietnamese: true);
        Assert(hwVm.Checks.Count == 6
            && hwVm.Options.Count == 4
            && hwVm.PrimaryEdition.Contains("Windows 11", StringComparison.OrdinalIgnoreCase),
            "HardwareCheckViewModel must populate 6 checks, 4 edition options, and primary recommendation.");

        await inPlaceVm.Hardware.RefreshAsync(isVietnamese: true);
        inPlaceVm.ActiveMainTab = MainSectionTab.HardwareCheck;
        Assert(inPlaceVm.IsHardwareMainTab
            && inPlaceVm.FooterStatusText == inPlaceVm.Hardware.PrimaryEdition
            && inPlaceVm.AppVersionText == "Felix WinDeploy v2.0",
            "HardwareCheck tab must surface PrimaryEdition in FooterStatusText and expose AppVersionText.");

        inPlaceVm.SetLanguage(false);
        inPlaceVm.Target.TryAutoSelectDefaultTarget();
        Assert(!inPlaceVm.StatusText.Contains("..", StringComparison.Ordinal) && inPlaceVm.StatusText.Length > 10,
            "StatusText must remain valid after switching language.");

        var findVm = new FindSourceViewModel(new PeAndOsSourceScanner(), new PeAndOsImageInspector());
        await findVm.StartAsync();
        Assert(findVm.Results.Count == 1
            && findVm.Results[0].Candidate.Path == @"D:\ISOs\Win11_And_PE.iso"
            && findVm.Results[0].EditionCount == 1
            && !findVm.HasError
            && !findVm.IsEmpty,
            "FindSourceViewModel must filter out WindowsPE images and keep installable Windows editions.");

        Console.WriteLine("PASS ViewModelTests");
    }

    private sealed class FakeBootTarget : IWindowsBootTarget
    {
        public List<BootToolCommand> RanCommands { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(BootToolCommand command)
        {
            RanCommands.Add(command);
            if (RanCommands.Count == 1 && command.Arguments.Contains("/bootex"))
            {
                return Task.FromResult((1, "unsupported /bootex"));
            }
            if (command.Tool.Equals("bcdedit.exe", StringComparison.OrdinalIgnoreCase) &&
                command.Arguments.Contains("/enum"))
            {
                var isDefault = command.Arguments.Contains("{default}");
                var output = isDefault
                    ? "identifier {11111111-1111-1111-1111-111111111111}\ndevice partition=W:\nosdevice partition=W:\nsystemroot \\Windows\npath \\Windows\\system32\\winload.efi\n"
                    : "identifier {9dea862c-5cdd-4e70-acc1-f32b344d4795}\ndevice partition=S:\npath \\EFI\\Microsoft\\Boot\\bootmgfw.efi\n";
                return Task.FromResult((0, output));
            }
            return Task.FromResult((0, "ok"));
        }

        public bool HasFile(string path) => true;
    }

    internal sealed class FakeDriverStoreService : IDriverStoreService
    {
        public Task<IReadOnlyList<InstalledDriverEntry>> ListThirdPartyDriversAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledDriverEntry>>(
            [
                new InstalledDriverEntry("oem1.inf", "iaStorVD.inf", false, "SCSIAdapter", "Intel", "2025-03-10", "20.1.0.10"),
                new InstalledDriverEntry("oem2.inf", "e1d68x64.inf", false, "Net", "Intel", "2025-02-01", "12.19.2.0"),
                new InstalledDriverEntry("oem3.inf", "nv_dispig.inf", false, "Display", "NVIDIA", "2025-04-01", "32.0.15.0")
            ]);

        public Task<DriverExportResult> ExportSelectedDriversAsync(
            IReadOnlyList<InstalledDriverEntry> drivers,
            string destinationDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverExportResult(drivers.Count, []));

        public Task<DriverInjectionResult> InjectOfflineDriversAsync(
            string windowsRoot,
            string driversFolder,
            bool recurse,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverInjectionResult(true, []));

        public Task<DriverInjectionResult> LoadWinPeDriversAsync(
            string winPeDriversFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriverInjectionResult(true, []));
    }

    private static TargetDiskFacts GptWindowsDisk(int number, long windowsOffset = 1L << 30) =>
        new(number, $"id{number}", $"ser{number}", "Samsung SSD", "NVMe", "GPT", 128L << 30,
            false, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 260L << 20, "", "EFI", "FAT32", "g1", "s1", true, false, false, false, false, false, false),
                new(2, windowsOffset, 60L << 30, "M", "Windows", "NTFS", "g2", "s2", false, false, false, false, false, false, true, 20L << 30),
                new(3, 62L << 30, 1L << 30, "", "Recovery", "NTFS", "g3", "s3", false, false, true, false, false, false, false),
                new(4, 64L << 30, 20L << 30, "N", "Data", "NTFS", "g4", "s4", false, false, false, false, false, false, false, 10L << 30)
            ]);

    private static TargetDiskFacts GptDiskWithoutRecovery(int number) =>
        new(number, $"id{number}", $"ser{number}", "WD Black", "NVMe", "GPT", 128L << 30,
            false, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 260L << 20, "", "EFI", "FAT32", "gr1", "sr1", true, false, false, false, false, false, false),
                new(2, 1L << 30, 60L << 30, "M", "Windows", "NTFS", "gr2", "sr2", false, false, false, false, false, false, true, 20L << 30),
                new(3, 62L << 30, 20L << 30, "N", "Data", "NTFS", "gr3", "sr3", false, false, false, false, false, false, false, 10L << 30)
            ]);

    private static TargetDiskFacts UsbDataDisk(int number, string letter) =>
        new(number, $"id{number}", $"ser{number}", "USB Drive", "USB", "GPT", 64L << 30,
            false, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 60L << 30, letter, "SetupMedia", "NTFS", "gu1", "su1", false, false, false, false, false, false, false, 30L << 30)
            ]);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeTargetSource(IReadOnlyList<TargetDiskFacts> initial) : ITargetSource
    {
        public IReadOnlyList<TargetDiskFacts> Next { get; set; } = initial;
        public Task<IReadOnlyList<TargetDiskFacts>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Next);
    }

    private sealed class FakeImageInspector : IImageInspector
    {
        public Task<WindowsImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsImageInspection(path,
            [
                new WindowsImageEdition(1, "Windows 11 Home", "x64", "10.0.26100", 1),
                new WindowsImageEdition(6, "Windows 11 Pro", "x64", "10.0.26100", 1)
            ]));
    }

    private sealed class InPlaceImageInspector : IImageInspector
    {
        public Task<WindowsImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsImageInspection(path,
            [
                new WindowsImageEdition(1, "Windows 11 Pro (Felix Backup - InPlace)", "x64", "10.0.26100", 1, 18L << 30)
            ]));
    }

    private sealed class FakeWindowsBackupService : IWindowsBackupService
    {
        public Task<IReadOnlyList<BackupSourceInstallation>> DiscoverSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupSourceInstallation>>(
            [
                new BackupSourceInstallation(@"M:\", "Windows 11 Pro", "24H2 / 26100", IsCurrentRunningWindows: true, 0, 2, false)
            ]);

        public bool IsSystemRebootPending() => true;
        public bool IsSystemInAuditMode() => false;

        public Task<string> CaptureDirectAsync(WindowsBackupRequest request, IProgress<(int Percent, string Message)>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.OutputPath);

        public Task<WindowsBackupSession> PrepareWinPeOrSysprepBackupAsync(WindowsBackupRequest request, IProgress<(int Percent, string Message)>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsBackupSession { OutputPath = request.OutputPath });
    }

    private sealed class FakeSourceScanner : ISourceScanner
    {
        public async IAsyncEnumerable<SourceCandidate> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class PeAndOsSourceScanner : ISourceScanner
    {
        public async IAsyncEnumerable<SourceCandidate> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new SourceCandidate(@"D:\ISOs\Win11_And_PE.iso", SourceImageKind.Iso, 5L << 30);
        }
    }

    private sealed class PeAndOsImageInspector : IImageInspector
    {
        public Task<WindowsImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsImageInspection(path,
            [
                new WindowsImageEdition(1, "Windows PE", "x64", "10.0.22621", 1, IsWindowsPe: true),
                new WindowsImageEdition(2, "Windows 11 Pro", "x64", "10.0.26100", 1, IsWindowsPe: false)
            ]));
    }

    internal sealed class FakeHardwareProfileService : IHardwareProfileService
    {
        public Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareSnapshot(
                "AMD Ryzen 7 7800X3D", 8, 16, 4200, "x64", new CpuCapabilities(true, true),
                32L << 30, "NVIDIA GeForce RTX 4070", 12L << 30, "B650 AORUS", "F21",
                FirmwareMode.UefiGpt, true, true, "TPM 2.0",
                [new HardwareDiskSummary(0, "NVMe 1TB", "NVMe", true, 1000L << 30, "GPT")]));
    }
}
