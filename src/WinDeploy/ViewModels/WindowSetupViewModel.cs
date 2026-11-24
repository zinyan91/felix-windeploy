using System.Collections.ObjectModel;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public enum SetupTab
{
    Source,
    Target,
    Options
}

public enum MainSectionTab
{
    InstallWindows,
    BackupWindows,
    HardwareCheck
}

public sealed class WindowSetupViewModel : ObservableObject
{
    private readonly CpuCapabilities cpu;
    private readonly bool isWinPe;
    private readonly INetworkAndPrinterBackupService networkBackup;
    private readonly IFirstBootStagingService firstBootStaging;
    private readonly IDesktopHandoffService desktopHandoff;
    private SetupTab activeTab = SetupTab.Source;
    private MainSectionTab activeMainTab = MainSectionTab.InstallWindows;
    private bool isVietnamese = true;
    private bool isSourceExpanded = true;
    private bool isTargetExpanded = true;
    private bool isOptionsExpanded = true;
    private string? answerPath;
    private bool useGeneratedUnattend;
    private UnattendSpec? generatedUnattend;
    private string? felixPeSourcePath;
    private bool verifyImage;
    private bool bypassMicrosoftAccount = true;
    private bool clearBootMenu;
    private bool restartWhenComplete;
    private bool exportConnectedWifi;
    private bool backupPrinters;
    private DriverBackupPreset driverBackupPreset = DriverBackupPreset.None;
    private IReadOnlyList<string> selectedDriverPublishedNames = [];
    private string? windowsDriversFolder;
    private string? winPeDriversFolder;
    private bool createNewRecovery = true;
    private bool addParallelWindows;
    private string? extraWinPePath;
    private string extraWinPeTitle = "Felix WinPE";
    private IReadOnlyList<RemoteAppSelection> remoteApps = [];
    private FirstDesktopAppMode firstDesktopMode = FirstDesktopAppMode.None;
    private string? firstDesktopSourcePath;
    private string? firstDesktopZipProgram;
    private bool installAppsSilently = true;
    private bool writeDesktopSummaryReport = true;
    private bool isBloatwareEnabled;
    private bool blockPromotedApps = true;
    private IReadOnlyList<string> selectedBloatwarePrefixes =
        BloatwareCatalog.DefaultApps.Where(a => a.DefaultSelected).Select(a => a.PackagePrefix).ToArray();
    private WinPeRecordingOptions recordingOptions = new();
    private RescueUsbCandidate? selectedRescueUsbOption;
    private bool isBusy;
    private int progressValue;
    private string progressText = "Ready";
    private InstallRunStage? currentStage;
    private InstallStageState? currentStageState;
    private string? installLogPath;
    private bool hasInstallActivity;

    public WindowSetupViewModel()
        : this(
            new NativeTargetSource(),
            new WindowsSourceInspector(),
            new NativeSourceScanner(new NativeTargetSource()),
            FirmwareDetector.Current(),
            PeEnvironment.IsWinPe,
            HardwareCapabilityDetector.CurrentCpu())
    {
    }

    public WindowSetupViewModel(
        ITargetSource targetSource,
        IImageInspector imageInspector,
        ISourceScanner sourceScanner,
        FirmwareMode bootFirmware,
        bool isWinPe,
        CpuCapabilities? cpu = null,
        INetworkAndPrinterBackupService? networkBackup = null,
        IDriverStoreService? driverStore = null,
        IExtraBootEntriesService? extraBootEntries = null,
        IFirstBootStagingService? firstBootStaging = null,
        bool? allowDesktopHandoff = null,
        IDesktopHandoffService? desktopHandoff = null,
        IWindowsBackupService? backupService = null,
        IHardwareProfileService? hardwareService = null)
    {
        this.cpu = cpu ?? HardwareCapabilityDetector.CurrentCpu();
        this.isWinPe = isWinPe;
        this.networkBackup = networkBackup ?? new NativeNetworkAndPrinterBackupService();
        this.firstBootStaging = firstBootStaging ?? new NativeFirstBootStagingService();
        this.desktopHandoff = desktopHandoff ?? new NativeDesktopHandoffService();
        Backup = new WindowsBackupViewModel(backupService ?? new NativeWindowsBackupService(), isWinPe);
        DriverStore = driverStore ?? new NativeDriverStoreService();
        ExtraBootEntries = extraBootEntries ?? new NativeExtraBootEntriesService();
        ImageInspector = imageInspector;
        SourceScanner = sourceScanner;
        var handoffEnabled = allowDesktopHandoff ?? !isWinPe;
        Target = new TargetViewModel(targetSource, bootFirmware, isWinPe, handoffEnabled);
        Source = new SourceViewModel(imageInspector, Target);
        Hardware = new HardwareCheckViewModel(hardwareService ?? new NativeHardwareProfileService(targetSource));
        felixPeSourcePath = handoffEnabled ? FelixPeLocator.TryResolve(null)?.DisplayPath : null;
        if (!isWinPe)
        {
            driverBackupPreset = DriverBackupPreset.Recommended;
            exportConnectedWifi = true;
            clearBootMenu = true;
            useGeneratedUnattend = true;
            generatedUnattend = new UnattendSpec(
                Password: UnattendGenerator.GenerateRandomPassword(),
                ClearPasswordOnFirstLogon: true);
        }

        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SourceViewModel.ImagePath) or nameof(SourceViewModel.Edition) or nameof(SourceViewModel.IsValid))
            {
                if (e.PropertyName == nameof(SourceViewModel.ImagePath))
                {
                    Target.UpdateSetupFiles(SetupFileLetters.Of(Source.ImagePath, AppContext.BaseDirectory, AnswerPath));
                    RebuildRescueUsbOptions();
                }
                Raise(nameof(IsInPlaceBackupSource));
                Raise(nameof(IsInstallCustomizationAllowed));
                Raise(nameof(SourceCompatibilityError));
                Raise(nameof(HasSourceCompatibilityError));
                Raise(nameof(FelixPeValidationError));
                Raise(nameof(HasFelixPeValidationError));
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
                Raise(nameof(FooterStatusText));
            }
        };
        Target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TargetViewModel.IsValid)
                or nameof(TargetViewModel.Summary)
                or nameof(TargetViewModel.Mode)
                or nameof(TargetViewModel.Roles)
                or nameof(TargetViewModel.EraseDisk)
                or nameof(TargetViewModel.SelectedDiskHasExistingRecovery))
            {
                Raise(nameof(CanCreateNewRecovery));
                Raise(nameof(ExecutionMode));
                Raise(nameof(IsDesktopHandoffMode));
                Raise(nameof(FelixPeValidationError));
                Raise(nameof(HasFelixPeValidationError));
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
                Raise(nameof(FooterStatusText));
            }
        };
        Backup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WindowsBackupViewModel.OutputPath) or nameof(WindowsBackupViewModel.Method))
            {
                Raise(nameof(FooterStatusText));
            }
        };
        Hardware.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HardwareCheckViewModel.PrimaryEdition))
            {
                Raise(nameof(FooterStatusText));
            }
        };
        Target.Disks.CollectionChanged += (_, _) => RebuildRescueUsbOptions();
        RebuildRescueUsbOptions();
    }

    public bool IsWinPe => isWinPe;
    public bool IsDesktopHandoffMode => !isWinPe && Target.AllowDesktopHandoff;
    public IDesktopHandoffService DesktopHandoff => desktopHandoff;
    public WindowsBackupViewModel Backup { get; }
    public HardwareCheckViewModel Hardware { get; }
    public string AppVersionText => "Felix WinDeploy v2.0";

    public bool IsInPlaceBackupSource =>
        WindowsBackupMetadata.IsInPlaceBackupEdition(Source.Edition?.Name)
        || WindowsBackupMetadata.IsInPlaceBackupEdition(Source.Edition?.DisplayName);

    public bool IsInstallCustomizationAllowed => !IsInPlaceBackupSource;

    public void SetLanguage(bool vietnamese)
    {
        if (isVietnamese != vietnamese)
        {
            isVietnamese = vietnamese;
            Raise(nameof(StatusText));
            Raise(nameof(FooterStatusText));
        }

        Hardware.SetLanguage(vietnamese);
    }

    public bool IsSelectedDiskVirtual => Target.IsEraseMode
        ? Target.EraseDisk?.Facts.IsVirtual == true
        : Target.Roles is not null && Target.Disks.Any(d =>
            d.Facts.IsVirtual &&
            d.Cards.Any(c => c.Facts.Number == Target.Roles.Windows.Number && c.Facts.Offset == Target.Roles.Windows.Offset));

    public SetupExecutionMode ExecutionMode => IsDesktopHandoffMode && !IsSelectedDiskVirtual
        ? SetupExecutionMode.DesktopHandoff
        : SetupExecutionMode.DirectWinPe;

    public string? FelixPeSourcePath
    {
        get => felixPeSourcePath;
        set
        {
            if (SetField(ref felixPeSourcePath, value))
            {
                Raise(nameof(FelixPeValidationError));
                Raise(nameof(HasFelixPeValidationError));
                Raise(nameof(CanStart));
            }
        }
    }

    public string? FelixPeValidationError =>
        ExecutionMode == SetupExecutionMode.DesktopHandoff && FelixPeLocator.TryResolve(FelixPeSourcePath, Source.ImagePath) is null
            ? "No FelixPE or WinRE boot image could be resolved."
            : null;

    public bool HasFelixPeValidationError => !string.IsNullOrWhiteSpace(FelixPeValidationError);

    public MainSectionTab ActiveMainTab
    {
        get => activeMainTab;
        set
        {
            if (SetField(ref activeMainTab, value))
            {
                Raise(nameof(IsInstallMainTab));
                Raise(nameof(IsBackupMainTab));
                Raise(nameof(IsHardwareMainTab));
                Raise(nameof(IsInstallOrBackupTab));
                Raise(nameof(FooterStatusText));
            }
        }
    }

    public bool IsInstallMainTab => ActiveMainTab == MainSectionTab.InstallWindows;
    public bool IsBackupMainTab => ActiveMainTab == MainSectionTab.BackupWindows;
    public bool IsHardwareMainTab => ActiveMainTab == MainSectionTab.HardwareCheck;
    public bool IsInstallOrBackupTab => IsInstallMainTab || IsBackupMainTab;

    public bool IsSourceExpanded
    {
        get => isSourceExpanded;
        set => SetField(ref isSourceExpanded, value);
    }

    public bool IsTargetExpanded
    {
        get => isTargetExpanded;
        set => SetField(ref isTargetExpanded, value);
    }

    public bool IsOptionsExpanded
    {
        get => isOptionsExpanded;
        set => SetField(ref isOptionsExpanded, value);
    }

    public bool IsDriverBackupActive =>
        DriverBackupPreset != DriverBackupPreset.None || SelectedDriverPublishedNames.Count > 0;

    public bool IsRemoteAppsActive => HasEnabledRemoteApps;

    public bool IsBloatwareEnabled
    {
        get => isBloatwareEnabled;
        set
        {
            if (SetField(ref isBloatwareEnabled, value))
            {
                Raise(nameof(IsBloatwareActive));
            }
        }
    }

    public bool BlockPromotedApps
    {
        get => blockPromotedApps;
        set => SetField(ref blockPromotedApps, value);
    }

    public IReadOnlyList<string> SelectedBloatwarePrefixes
    {
        get => selectedBloatwarePrefixes;
        private set => SetField(ref selectedBloatwarePrefixes, value);
    }

    public bool IsBloatwareActive => IsBloatwareEnabled;

    public WinPeRecordingOptions RecordingOptions
    {
        get => recordingOptions;
        private set
        {
            if (SetField(ref recordingOptions, value))
            {
                Raise(nameof(IsRecordingActive));
            }
        }
    }

    public bool IsRecordingActive => RecordingOptions.IsActive;

    public void ApplyRecordingOptions(WinPeRecordingOptions options)
    {
        RecordingOptions = options;
    }

    public ObservableCollection<RescueUsbCandidate> RescueUsbOptions { get; } = [];

    public RescueUsbCandidate? SelectedRescueUsbOption
    {
        get => selectedRescueUsbOption;
        set
        {
            if (SetField(ref selectedRescueUsbOption, value))
            {
                Raise(nameof(CanMakeStandaloneRescueUsb));
            }
        }
    }

    public bool CanMakeStandaloneRescueUsb => SelectedRescueUsbOption is { DiskNumber: >= 0, IsEligible: true };

    public void RebuildRescueUsbOptions()
    {
        var previousNumber = SelectedRescueUsbOption?.DiskNumber ?? -1;
        var protectedLetters = SetupFileLetters.Of(Source.ImagePath, AppContext.BaseDirectory, AnswerPath);
        RescueUsbOptions.Clear();

        var none = new RescueUsbCandidate(-1, "", "", "", 0, RescueUsbMode.None, null);
        RescueUsbOptions.Add(none);

        foreach (var diskRow in Target.Disks)
        {
            if (diskRow.Facts.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = RescueUsbPlanner.EvaluateUsbDisk(diskRow.Facts, protectedLetters);
                if (candidate.IsEligible)
                {
                    RescueUsbOptions.Add(candidate);
                }
            }
        }

        SelectedRescueUsbOption = RescueUsbOptions.FirstOrDefault(o => o.DiskNumber == previousNumber) ?? none;
    }

    public void ApplyBloatwareSelection(bool enabled, bool blockPromoted, IReadOnlyList<string> selectedPrefixes)
    {
        IsBloatwareEnabled = enabled;
        BlockPromotedApps = blockPromoted;
        SelectedBloatwarePrefixes = selectedPrefixes;
    }

    public bool UseGeneratedUnattend
    {
        get => useGeneratedUnattend && string.IsNullOrWhiteSpace(AnswerPath);
        private set
        {
            if (SetField(ref useGeneratedUnattend, value))
            {
                Raise(nameof(IsGeneratedUnattendActive));
                Raise(nameof(StatusText));
                Raise(nameof(FooterStatusText));
            }
        }
    }

    public bool IsGeneratedUnattendActive => UseGeneratedUnattend;
    public UnattendSpec? GeneratedUnattendSpec => generatedUnattend;

    public void ApplyGeneratedUnattend(UnattendSpec spec, string? savedFilePath = null)
    {
        generatedUnattend = spec;
        if (!string.IsNullOrWhiteSpace(savedFilePath) && File.Exists(savedFilePath))
        {
            useGeneratedUnattend = false;
            AnswerPath = savedFilePath;
        }
        else
        {
            useGeneratedUnattend = true;
            AnswerPath = null;
        }

        Raise(nameof(UseGeneratedUnattend));
        Raise(nameof(IsGeneratedUnattendActive));
        Raise(nameof(StatusText));
        Raise(nameof(FooterStatusText));
    }

    public void ClearUnattend()
    {
        useGeneratedUnattend = false;
        generatedUnattend = null;
        AnswerPath = null;
        Raise(nameof(UseGeneratedUnattend));
        Raise(nameof(IsGeneratedUnattendActive));
        Raise(nameof(StatusText));
        Raise(nameof(FooterStatusText));
    }

    public string CurrentArchitectureLabel => Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public string EnvironmentLabel => isWinPe ? "WinPE" : "Windows Desktop";

    public SourceViewModel Source { get; }
    public TargetViewModel Target { get; }
    public ISourceScanner SourceScanner { get; }
    public IImageInspector ImageInspector { get; }
    public IDriverStoreService DriverStore { get; }
    public IExtraBootEntriesService ExtraBootEntries { get; }
    public ObservableCollection<InstallProgressUpdate> InstallProgress { get; } = [];

    public SetupTab ActiveTab
    {
        get => activeTab;
        set
        {
            if (SetField(ref activeTab, value))
            {
                Raise(nameof(IsSourceTab));
                Raise(nameof(IsTargetTab));
                Raise(nameof(IsOptionsTab));
            }
        }
    }

    public bool IsSourceTab => ActiveTab == SetupTab.Source;
    public bool IsTargetTab => ActiveTab == SetupTab.Target;
    public bool IsOptionsTab => ActiveTab == SetupTab.Options;

    public string? AnswerPath
    {
        get => answerPath;
        set
        {
            if (SetField(ref answerPath, value))
            {
                Target.UpdateSetupFiles(SetupFileLetters.Of(Source.ImagePath, AppContext.BaseDirectory, answerPath));
                Raise(nameof(StatusText));
                Raise(nameof(FooterStatusText));
                Raise(nameof(CanStart));
            }
        }
    }

    public bool VerifyImage
    {
        get => verifyImage;
        set => SetField(ref verifyImage, value);
    }

    public bool BypassMicrosoftAccount
    {
        get => bypassMicrosoftAccount;
        set => SetField(ref bypassMicrosoftAccount, value);
    }

    public bool ClearBootMenu
    {
        get => clearBootMenu;
        set => SetField(ref clearBootMenu, value);
    }

    public bool RestartWhenComplete
    {
        get => restartWhenComplete;
        set => SetField(ref restartWhenComplete, value);
    }

    public bool ExportConnectedWifi
    {
        get => exportConnectedWifi;
        set => SetField(ref exportConnectedWifi, value);
    }

    public bool BackupPrinters
    {
        get => backupPrinters;
        set => SetField(ref backupPrinters, value);
    }

    public bool IsPrintBrmAvailable => networkBackup.IsPrintBrmAvailable();

    public DriverBackupPreset DriverBackupPreset
    {
        get => driverBackupPreset;
        set
        {
            if (SetField(ref driverBackupPreset, value))
            {
                Raise(nameof(IsDriverBackupActive));
            }
        }
    }

    public IReadOnlyList<string> SelectedDriverPublishedNames
    {
        get => selectedDriverPublishedNames;
        private set
        {
            if (SetField(ref selectedDriverPublishedNames, value))
            {
                Raise(nameof(SelectedDriverCount));
                Raise(nameof(HasSelectedDrivers));
                Raise(nameof(IsDriverBackupActive));
            }
        }
    }

    public int SelectedDriverCount => SelectedDriverPublishedNames.Count;
    public bool HasSelectedDrivers => SelectedDriverPublishedNames.Count > 0;

    public string? WindowsDriversFolder
    {
        get => windowsDriversFolder;
        set
        {
            if (SetField(ref windowsDriversFolder, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
            }
        }
    }

    public string? WinPeDriversFolder
    {
        get => winPeDriversFolder;
        set
        {
            if (SetField(ref winPeDriversFolder, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
            }
        }
    }

    public void ApplyDriverSelection(DriverBackupPreset preset, IReadOnlyList<string> selectedPublishedNames)
    {
        DriverBackupPreset = preset;
        SelectedDriverPublishedNames = selectedPublishedNames;
    }

    public bool CreateNewRecovery
    {
        get => createNewRecovery;
        set
        {
            if (SetField(ref createNewRecovery, value))
            {
                Raise(nameof(CanStart));
            }
        }
    }

    public bool CanCreateNewRecovery =>
        Target.IsEraseMode || (Target.Roles?.Recovery is null && !Target.SelectedDiskHasExistingRecovery);

    public bool AddParallelWindows
    {
        get => addParallelWindows;
        set => SetField(ref addParallelWindows, value);
    }

    public string? ExtraWinPePath
    {
        get => extraWinPePath;
        set
        {
            if (SetField(ref extraWinPePath, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
            }
        }
    }

    public string ExtraWinPeTitle
    {
        get => extraWinPeTitle;
        set => SetField(ref extraWinPeTitle, string.IsNullOrWhiteSpace(value) ? "Felix WinPE" : value.Trim());
    }

    public IReadOnlyList<RemoteAppSelection> RemoteApps
    {
        get => remoteApps;
        private set
        {
            if (SetField(ref remoteApps, value))
            {
                Raise(nameof(EnabledRemoteAppCount));
                Raise(nameof(HasEnabledRemoteApps));
                Raise(nameof(CanStart));
            }
        }
    }

    public int EnabledRemoteAppCount => RemoteApps.Count(a => a.Enabled);
    public bool HasEnabledRemoteApps => EnabledRemoteAppCount > 0;

    public void ApplyRemoteApps(IReadOnlyList<RemoteAppSelection> selections)
    {
        RemoteApps = selections;
        Raise(nameof(IsRemoteAppsActive));
    }

    public ObservableCollection<string> ZipPrograms { get; } = [];

    public FirstDesktopAppMode FirstDesktopMode
    {
        get => firstDesktopMode;
        set
        {
            if (SetField(ref firstDesktopMode, value))
            {
                Raise(nameof(HasFirstDesktopSource));
                Raise(nameof(IsFirstDesktopZipMode));
                RefreshZipPrograms();
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
            }
        }
    }

    public bool HasFirstDesktopSource => FirstDesktopMode != FirstDesktopAppMode.None;
    public bool IsFirstDesktopZipMode => FirstDesktopMode == FirstDesktopAppMode.ZipArchive;

    public string? FirstDesktopSourcePath
    {
        get => firstDesktopSourcePath;
        set
        {
            if (SetField(ref firstDesktopSourcePath, value))
            {
                RefreshZipPrograms();
                Raise(nameof(CanStart));
                Raise(nameof(StatusText));
            }
        }
    }

    public string? FirstDesktopZipProgram
    {
        get => firstDesktopZipProgram;
        set
        {
            if (SetField(ref firstDesktopZipProgram, value))
            {
                Raise(nameof(CanStart));
            }
        }
    }

    public bool InstallAppsSilently
    {
        get => installAppsSilently;
        set => SetField(ref installAppsSilently, value);
    }

    public bool WriteDesktopSummaryReport
    {
        get => writeDesktopSummaryReport;
        set => SetField(ref writeDesktopSummaryReport, value);
    }

    private void RefreshZipPrograms()
    {
        ZipPrograms.Clear();
        if (!IsFirstDesktopZipMode || string.IsNullOrWhiteSpace(FirstDesktopSourcePath))
        {
            return;
        }

        foreach (var exe in ZipProgramInspector.ListExecutables(FirstDesktopSourcePath.Trim()))
        {
            ZipPrograms.Add(exe);
        }

        if (ZipPrograms.Count > 0
            && (string.IsNullOrWhiteSpace(FirstDesktopZipProgram)
                || !ZipPrograms.Contains(FirstDesktopZipProgram, StringComparer.OrdinalIgnoreCase)))
        {
            FirstDesktopZipProgram = ZipPrograms[0];
        }
    }

    public string? SourceCompatibilityError =>
        Source.Edition is not null &&
        HardwareRequirements.RequiresPopcntAndSse42(Source.Edition.Architecture, Source.Edition.Version) &&
        (!cpu.HasPopcnt || !cpu.HasSse42)
            ? "Windows build 26100 or newer requires a CPU with POPCNT and SSE4.2."
            : null;

    public bool HasSourceCompatibilityError => !string.IsNullOrWhiteSpace(SourceCompatibilityError);

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(CanRefresh));
            }
        }
    }

    public bool CanStart => !IsBusy && Source.IsValid && Target.IsValid &&
        (ExecutionMode != SetupExecutionMode.DesktopHandoff || FelixPeLocator.TryResolve(FelixPeSourcePath, Source.ImagePath) is not null) &&
        WindowSetupPreflight.Validate(CreateRequest(inspectFiles: false), cpu).CanProceed;
    public bool CanRefresh => !IsBusy;

    public int ProgressValue
    {
        get => progressValue;
        private set => SetField(ref progressValue, value);
    }

    public string ProgressText
    {
        get => progressText;
        private set => SetField(ref progressText, value);
    }

    public InstallRunStage? CurrentStage
    {
        get => currentStage;
        private set => SetField(ref currentStage, value);
    }

    public InstallStageState? CurrentStageState
    {
        get => currentStageState;
        private set => SetField(ref currentStageState, value);
    }

    public string? InstallLogPath
    {
        get => installLogPath;
        private set => SetField(ref installLogPath, value);
    }

    public bool HasInstallActivity
    {
        get => hasInstallActivity;
        private set => SetField(ref hasInstallActivity, value);
    }

    public string StatusText
    {
        get
        {
            var source = Source.IsValid
                ? Source.Edition?.Name
                : (isVietnamese ? "Chưa chọn bộ cài" : "No source");
            var target = Target.IsValid
                ? (Target.IsEraseMode
                    ? (isVietnamese ? $"Xoá ổ {Target.EraseDisk?.Facts.Number}" : $"Erase Disk {Target.EraseDisk?.Facts.Number}")
                    : FormatPartitionShort(Target.Roles!.Windows, isVietnamese))
                : (isVietnamese ? "Chưa chọn đích" : "No target");
            var unattend = !string.IsNullOrWhiteSpace(AnswerPath)
                ? Path.GetFileName(AnswerPath)
                : (UseGeneratedUnattend ? (isVietnamese ? "Unattend tự động" : "Auto Unattend") : "OOBE");
            return $"{source} · {target} · {unattend}";
        }
    }

    public string FooterStatusText => ActiveMainTab switch
    {
        MainSectionTab.BackupWindows => string.IsNullOrWhiteSpace(Backup.OutputPath)
            ? (isVietnamese ? "Chọn đích sao lưu Windows" : "Select a backup destination")
            : Backup.OutputPath,
        MainSectionTab.HardwareCheck => Hardware.PrimaryEdition,
        _ => StatusText
    };

    private static string FormatPartitionShort(TargetPartitionFacts partition, bool vietnamese)
    {
        if (!string.IsNullOrWhiteSpace(partition.Label))
        {
            return string.IsNullOrWhiteSpace(partition.Letter)
                ? partition.Label
                : $"{partition.Letter}: {partition.Label}";
        }

        return string.IsNullOrWhiteSpace(partition.Letter)
            ? (vietnamese ? $"Phân vùng {partition.Number}" : $"Partition {partition.Number}")
            : $"{partition.Letter}:";
    }

    public PreflightResult Validate()
    {
        var result = WindowSetupPreflight.Validate(CreateRequest(), cpu);
        if (ExecutionMode == SetupExecutionMode.DesktopHandoff && FelixPeLocator.TryResolve(FelixPeSourcePath, Source.ImagePath) is null)
        {
            return new PreflightResult([.. result.Issues, new("felixpe.missing", "No FelixPE or WinRE boot image was found.", true)]);
        }

        return result;
    }

    public WindowSetupRequest CreateRequest(bool inspectFiles = true)
    {
        var (onTarget, isRemovable, isBitLocker) = EvaluateExtraWinPePlacement();
        var trimmedExtraWinPe = string.IsNullOrWhiteSpace(ExtraWinPePath) ? null : ExtraWinPePath.Trim();
        FirstDesktopAppOptions? firstDesktopApp = FirstDesktopMode == FirstDesktopAppMode.None
            ? null
            : new FirstDesktopAppOptions(
                FirstDesktopMode,
                string.IsNullOrWhiteSpace(FirstDesktopSourcePath) ? null : FirstDesktopSourcePath.Trim(),
                string.IsNullOrWhiteSpace(FirstDesktopZipProgram) ? null : FirstDesktopZipProgram.Trim());
        var lockCustomizations = IsInPlaceBackupSource;
        var effectiveAnswerPath = lockCustomizations ? null : AnswerPath;
        var effectiveFirstDesktop = lockCustomizations ? null : firstDesktopApp;
        var effectiveRemoteApps = lockCustomizations ? Array.Empty<RemoteAppSelection>() : RemoteApps;
        var effectiveBypassMs = !lockCustomizations && BypassMicrosoftAccount;
        var effectiveBloatware = lockCustomizations
            ? new BloatwareOptions(Enabled: false, BlockPromotedApps: false, RemovePackagePrefixes: [])
            : new BloatwareOptions(
                Enabled: IsBloatwareEnabled,
                BlockPromotedApps: BlockPromotedApps,
                RemovePackagePrefixes: SelectedBloatwarePrefixes);
        var effectiveGeneratedUnattend = lockCustomizations
            ? null
            : string.IsNullOrWhiteSpace(AnswerPath) && UseGeneratedUnattend ? generatedUnattend : null;

        var fileInspection = inspectFiles
            ? WindowSetupInputFiles.Inspect(
                Source.ImagePath,
                effectiveAnswerPath,
                WindowsDriversFolder,
                WinPeDriversFolder,
                trimmedExtraWinPe,
                effectiveFirstDesktop)
                with
                {
                    ExtraWinPeOnTarget = onTarget,
                    ExtraWinPeIsRemovable = isRemovable,
                    ExtraWinPeIsBitLocker = isBitLocker
                }
            : new InputFileInspection(true, true, true, null, null, true, true, true, onTarget, isRemovable, isBitLocker);

        var createRecovery = Target.IsEraseMode
            || Target.Roles?.Recovery is not null
            || (CreateNewRecovery && CanCreateNewRecovery);

        return new(
            new WindowsSource(
                Source.ImagePath,
                Source.Edition?.Index ?? 0,
                Source.Edition?.DisplayName ?? Source.Edition?.Name ?? "",
                Source.Edition?.Architecture ?? "",
                Source.Edition?.Version ?? ""),
            Target.IsValid
                ? Target.ToInstallTarget()
                : new InstallTarget(Target.Mode, "", Target.BootFirmware, new("", ""), new("", ""), new("", "")),
            effectiveAnswerPath,
            fileInspection,
            new InstallOptions(
                VerifyImage: VerifyImage,
                CreateRecovery: createRecovery,
                ClearBootMenu: ClearBootMenu,
                RestartWhenComplete: RestartWhenComplete,
                BypassMicrosoftAccount: effectiveBypassMs,
                Drivers: new DriverAndNetworkOptions(
                    BackupPreset: DriverBackupPreset,
                    SelectedDriverPublishedNames: SelectedDriverPublishedNames,
                    ExportConnectedWifi: ExportConnectedWifi,
                    BackupPrinters: BackupPrinters && IsPrintBrmAvailable,
                    WindowsDriversFolder: string.IsNullOrWhiteSpace(WindowsDriversFolder) ? null : WindowsDriversFolder.Trim(),
                    WinPeDriversFolder: string.IsNullOrWhiteSpace(WinPeDriversFolder) ? null : WinPeDriversFolder.Trim()),
                BootAndRecovery: new BootAndRecoveryOptions(
                    CreateNewRecovery: CreateNewRecovery && CanCreateNewRecovery,
                    AddParallelWindows: AddParallelWindows,
                    ExtraWinPePath: trimmedExtraWinPe,
                    ExtraWinPeTitle: ExtraWinPeTitle),
                FirstBoot: new FirstBootOptions(
                    RemoteApps: effectiveRemoteApps,
                    FirstDesktopApp: effectiveFirstDesktop,
                    InstallSilently: lockCustomizations || InstallAppsSilently,
                    WriteDesktopSummaryReport: WriteDesktopSummaryReport),
                Bloatware: effectiveBloatware,
                GeneratedUnattend: effectiveGeneratedUnattend,
                Recording: RecordingOptions,
                RescueUsbDiskNumber: SelectedRescueUsbOption is { DiskNumber: >= 0, IsEligible: true } u ? u.DiskNumber : null));
    }

    private (bool OnTarget, bool IsRemovable, bool IsBitLocker) EvaluateExtraWinPePlacement()
    {
        if (string.IsNullOrWhiteSpace(ExtraWinPePath))
        {
            return (false, false, false);
        }

        var trimmed = ExtraWinPePath.Trim();
        if (trimmed.Length < 2 || !char.IsLetter(trimmed[0]) || trimmed[1] != ':')
        {
            return (false, false, false);
        }

        var letter = char.ToUpperInvariant(trimmed[0]).ToString();
        foreach (var diskRow in Target.Disks)
        {
            var matchedPartition = diskRow.Facts.Partitions.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.Letter)
                && string.Equals(p.Letter.TrimEnd(':'), letter, StringComparison.OrdinalIgnoreCase));
            if (matchedPartition is null)
            {
                continue;
            }

            var isRemovable = diskRow.Facts.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase);
            var isBitLocker = matchedPartition.IsBitLocker;
            var onTarget = Target.IsEraseMode
                ? Target.EraseDisk?.Facts.Number == diskRow.Facts.Number
                : Target.Roles is not null
                    && matchedPartition.Number == Target.Roles.Windows.Number
                    && matchedPartition.Offset == Target.Roles.Windows.Offset;
            return (onTarget, isRemovable, isBitLocker);
        }

        return (false, false, false);
    }

    public async Task<InstallationOutcome> InstallAsync()
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("An installation is already running.");
        }

        IsBusy = true;
        HasInstallActivity = true;
        InstallProgress.Clear();
        ProgressValue = 0;
        ProgressText = "Starting";
        CurrentStage = InstallRunStage.Preflight;
        CurrentStageState = InstallStageState.Running;
        InstallLogPath = null;
        try
        {
            var platform = new WindowSetupInstallationPlatform(
                WindowSetupEngineLocator.Find(),
                DriverStore,
                networkBackup,
                ExtraBootEntries,
                firstBootStaging);
            var runner = new WindowInstallationRunner(platform);
            var progress = new Progress<InstallProgressUpdate>(update =>
            {
                ProgressValue = update.Percent;
                CurrentStage = update.Stage;
                CurrentStageState = update.State;
                ProgressText = update.Message;
                InstallProgress.Add(update);
            });
            var journalRoot = Path.Combine(
                Path.GetTempPath(),
                "FelixPE",
                "WindowSetup");
            var outcome = await runner.RunAsync(CreateRequest(), journalRoot, progress);
            InstallLogPath = outcome.ExportedLogDirectory ?? outcome.RunDirectory;
            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<PreparedDesktopHandoff> PrepareDesktopHandoffAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("An installation is already running.");
        }

        var peSource = FelixPeLocator.TryResolve(FelixPeSourcePath, Source.ImagePath)
            ?? throw new InvalidOperationException("No FelixPE or WinRE boot image was found.");

        IsBusy = true;
        HasInstallActivity = true;
        InstallProgress.Clear();
        ProgressValue = 0;
        ProgressText = "Preparing FelixPE handoff";
        CurrentStage = InstallRunStage.PrepareTarget;
        CurrentStageState = InstallStageState.Running;
        InstallLogPath = null;
        try
        {
            var progress = new Progress<InstallProgressUpdate>(update =>
            {
                ProgressValue = update.Percent;
                CurrentStage = update.Stage;
                CurrentStageState = update.State;
                ProgressText = update.Message;
                InstallProgress.Add(update);
            });
            return await desktopHandoff.PrepareAsync(
                CreateRequest(),
                peSource,
                DriverStore,
                networkBackup,
                firstBootStaging,
                progress,
                cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
