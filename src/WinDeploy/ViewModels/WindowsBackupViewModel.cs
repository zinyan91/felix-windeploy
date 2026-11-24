using System.Collections.ObjectModel;
using System.IO;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed class WindowsBackupViewModel : ObservableObject
{
    private readonly IWindowsBackupService backupService;
    private readonly bool isWinPe;
    private BackupSourceInstallation? selectedSource;
    private WindowsBackupMethod method = WindowsBackupMethod.LiveVssOrOfflineImage;
    private BackupArchiveFormat format = BackupArchiveFormat.Wim;
    private string outputPath = string.Empty;
    private string imageName = "Windows Backup";
    private string imageDescription = string.Empty;
    private bool makePortableAcrossHardware = true;
    private bool shutdownAfterCapture;
    private int splitPartSizeMiB = 3800;
    private bool sysprepGeneralize = true;
    private bool sysprepAuditMode;
    private bool stripThirdPartyDrivers;
    private bool stripStorageDrivers;
    private bool autoRestoreSafetyOnSuccess = true;
    private bool autoRestoreSafetyOnFailure = true;
    private string sysprepUnattendPath = string.Empty;
    private bool isBusy;
    private int progressValue;
    private string statusText = string.Empty;
    private WindowsBackupPreflight preflight = new(false, [], []);

    public WindowsBackupViewModel(IWindowsBackupService backupService, bool isWinPe)
    {
        this.backupService = backupService;
        this.isWinPe = isWinPe;
        if (isWinPe)
        {
            method = WindowsBackupMethod.LiveVssOrOfflineImage;
        }
        RefreshPreflight();
    }

    public ObservableCollection<BackupSourceInstallation> Sources { get; } = [];

    public BackupSourceInstallation? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (SetField(ref selectedSource, value))
            {
                if (value is not null && string.IsNullOrWhiteSpace(ImageName))
                {
                    ImageName = value.EditionName;
                }
                RefreshPreflight();
            }
        }
    }

    public WindowsBackupMethod Method
    {
        get => method;
        set
        {
            if (SetField(ref method, value))
            {
                Raise(nameof(IsLiveOrOfflineMethod));
                Raise(nameof(IsWinPeCaptureMethod));
                Raise(nameof(IsSysprepMethod));
                Raise(nameof(IsNonSysprepMethod));
                RefreshPreflight();
            }
        }
    }

    public bool IsLiveOrOfflineMethod
    {
        get => Method == WindowsBackupMethod.LiveVssOrOfflineImage;
        set
        {
            if (value)
            {
                Method = WindowsBackupMethod.LiveVssOrOfflineImage;
            }
        }
    }

    public bool IsWinPeCaptureMethod
    {
        get => Method == WindowsBackupMethod.WinPeOfflineCapture;
        set
        {
            if (value)
            {
                Method = WindowsBackupMethod.WinPeOfflineCapture;
            }
        }
    }

    public bool IsSysprepMethod
    {
        get => Method == WindowsBackupMethod.SysprepGeneralizeCapture;
        set
        {
            if (value)
            {
                Method = WindowsBackupMethod.SysprepGeneralizeCapture;
            }
        }
    }

    public bool IsNonSysprepMethod => Method != WindowsBackupMethod.SysprepGeneralizeCapture;
    public bool CanChooseSysprep => !isWinPe;

    public BackupArchiveFormat Format
    {
        get => format;
        set
        {
            if (SetField(ref format, value))
            {
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    OutputPath = WindowsBackupMetadata.NormalizeOutputPath(outputPath, value);
                }
                Raise(nameof(IsWimFormat));
                Raise(nameof(IsEsdFormat));
                Raise(nameof(IsSwmFormat));
                RefreshPreflight();
            }
        }
    }

    public bool IsWimFormat
    {
        get => Format == BackupArchiveFormat.Wim;
        set { if (value) Format = BackupArchiveFormat.Wim; }
    }

    public bool IsEsdFormat
    {
        get => Format == BackupArchiveFormat.Esd;
        set { if (value) Format = BackupArchiveFormat.Esd; }
    }

    public bool IsSwmFormat
    {
        get => Format == BackupArchiveFormat.Swm;
        set { if (value) Format = BackupArchiveFormat.Swm; }
    }

    public string OutputPath
    {
        get => outputPath;
        set
        {
            if (SetField(ref outputPath, value))
            {
                RefreshPreflight();
            }
        }
    }

    public string ImageName
    {
        get => imageName;
        set
        {
            if (SetField(ref imageName, value))
            {
                RefreshPreflight();
            }
        }
    }

    public string ImageDescription
    {
        get => imageDescription;
        set => SetField(ref imageDescription, value);
    }

    public bool MakePortableAcrossHardware
    {
        get => makePortableAcrossHardware;
        set => SetField(ref makePortableAcrossHardware, value);
    }

    public bool ShutdownAfterCapture
    {
        get => shutdownAfterCapture;
        set => SetField(ref shutdownAfterCapture, value);
    }

    public int SplitPartSizeMiB
    {
        get => splitPartSizeMiB;
        set
        {
            if (SetField(ref splitPartSizeMiB, value))
            {
                RefreshPreflight();
            }
        }
    }

    public bool SysprepGeneralize
    {
        get => sysprepGeneralize;
        set => SetField(ref sysprepGeneralize, value);
    }

    public bool SysprepAuditMode
    {
        get => sysprepAuditMode;
        set
        {
            if (SetField(ref sysprepAuditMode, value))
            {
                RefreshPreflight();
            }
        }
    }

    public bool StripThirdPartyDrivers
    {
        get => stripThirdPartyDrivers;
        set
        {
            if (SetField(ref stripThirdPartyDrivers, value))
            {
                if (!value)
                {
                    StripStorageDrivers = false;
                }
                RefreshPreflight();
            }
        }
    }

    public bool StripStorageDrivers
    {
        get => stripStorageDrivers;
        set
        {
            if (SetField(ref stripStorageDrivers, value))
            {
                RefreshPreflight();
            }
        }
    }

    public bool AutoRestoreSafetyOnSuccess
    {
        get => autoRestoreSafetyOnSuccess;
        set => SetField(ref autoRestoreSafetyOnSuccess, value);
    }

    public bool AutoRestoreSafetyOnFailure
    {
        get => autoRestoreSafetyOnFailure;
        set => SetField(ref autoRestoreSafetyOnFailure, value);
    }

    public string SysprepUnattendPath
    {
        get => sysprepUnattendPath;
        set => SetField(ref sysprepUnattendPath, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                Raise(nameof(CanStartBackup));
            }
        }
    }

    public int ProgressValue
    {
        get => progressValue;
        private set => SetField(ref progressValue, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public WindowsBackupPreflight Preflight
    {
        get => preflight;
        private set
        {
            if (SetField(ref preflight, value))
            {
                Raise(nameof(CanStartBackup));
                Raise(nameof(PreflightErrorText));
                Raise(nameof(PreflightWarningText));
            }
        }
    }

    public bool CanStartBackup => !IsBusy && Preflight.IsReady;
    public string PreflightErrorText => string.Join(Environment.NewLine, Preflight.Errors);
    public string PreflightWarningText => string.Join(Environment.NewLine, Preflight.Warnings);

    public async Task RefreshSourcesAsync(CancellationToken cancellationToken = default)
    {
        var list = await backupService.DiscoverSourcesAsync(cancellationToken);
        var previousRoot = SelectedSource?.RootPath;
        Sources.Clear();
        foreach (var item in list)
        {
            Sources.Add(item);
        }

        SelectedSource = Sources.FirstOrDefault(s =>
            !string.IsNullOrEmpty(previousRoot)
            && s.RootPath.Equals(previousRoot, StringComparison.OrdinalIgnoreCase))
            ?? Sources.FirstOrDefault();

        if (SelectedSource is not null && string.IsNullOrWhiteSpace(ImageName))
        {
            ImageName = $"{SelectedSource.EditionName} {DateTime.Now:yyyy-MM-dd}";
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            var nonOsDrive = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.IsReady
                    && d.DriveType == DriveType.Fixed
                    && !d.Name.Equals(SelectedSource?.RootPath, StringComparison.OrdinalIgnoreCase)
                    && !d.Name.StartsWith("X:", StringComparison.OrdinalIgnoreCase));
            if (nonOsDrive is not null)
            {
                OutputPath = WindowsBackupMetadata.NormalizeOutputPath(
                    Path.Combine(nonOsDrive.Name, "FelixBackups", $"WindowsBackup_{DateTime.Now:yyyyMMdd}.wim"),
                    Format);
            }
        }

        RefreshPreflight();
    }

    public WindowsBackupRequest CreateRequest() =>
        new(
            SelectedSource ?? new BackupSourceInstallation("", "", "", false),
            Method,
            Format,
            WindowsBackupMetadata.NormalizeOutputPath(OutputPath, Format),
            ImageName.Trim(),
            string.IsNullOrWhiteSpace(ImageDescription) ? null : ImageDescription.Trim(),
            MakePortableAcrossHardware: Method != WindowsBackupMethod.SysprepGeneralizeCapture && MakePortableAcrossHardware,
            ShutdownAfterCapture: ShutdownAfterCapture,
            SplitPartSizeMiB: SplitPartSizeMiB,
            Sysprep: Method == WindowsBackupMethod.SysprepGeneralizeCapture
                ? new SysprepBackupOptions(
                    Generalize: SysprepGeneralize,
                    EnterAuditMode: SysprepAuditMode,
                    StripThirdPartyDrivers: StripThirdPartyDrivers,
                    StripStorageDrivers: StripStorageDrivers,
                    AutoRestoreSafetyBackupOnSuccess: AutoRestoreSafetyOnSuccess,
                    AutoRestoreSafetyBackupOnFailure: AutoRestoreSafetyOnFailure,
                    FailureCountdownSeconds: 60,
                    UnattendXmlPath: string.IsNullOrWhiteSpace(SysprepUnattendPath) ? null : SysprepUnattendPath.Trim())
                : null);

    public void RefreshPreflight()
    {
        var req = CreateRequest();
        var rebootPending = backupService.IsSystemRebootPending();
        var inAudit = backupService.IsSystemInAuditMode();
        Preflight = WindowsBackupPreflightValidator.Validate(req, isWinPe, rebootPending, inAudit);
    }

    public async Task<(bool DirectCompleted, string? OutputFile, WindowsBackupSession? HandoffSession)> RunBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest();
        IsBusy = true;
        ProgressValue = 0;
        var progress = new Progress<(int Percent, string Message)>(p =>
        {
            ProgressValue = p.Percent;
            StatusText = p.Message;
        });

        try
        {
            if (request.Method == WindowsBackupMethod.LiveVssOrOfflineImage)
            {
                var outFile = await backupService.CaptureDirectAsync(request, progress, cancellationToken);
                return (true, outFile, null);
            }

            var session = await backupService.PrepareWinPeOrSysprepBackupAsync(request, progress, cancellationToken);
            return (false, null, session);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
