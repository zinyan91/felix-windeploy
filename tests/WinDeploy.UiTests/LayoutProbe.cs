using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Felix.UI.Contracts;
using Felix.UI.Services;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;
using Felix.WindowSetup.ViewModels;
using Felix.WindowSetup.Views;

namespace Felix.WindowSetup.UiTests;

internal static class LayoutProbe
{
    public static void Run(string? captureDir = null)
    {
        var app = WpfTestHost.EnsureApp();
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            Directory.CreateDirectory(captureDir);
        }

        var targets = new FakeTargetSource(
        [
            GptWindowsDisk(0),
            PeUsbDisk(1),
            MbrDataDisk(2)
        ]);
        var vm = new WindowSetupViewModel(
            targets,
            new FakeImageInspector(),
            new FakeSourceScanner(),
            FirmwareMode.UefiGpt,
            isWinPe: true,
            hardwareService: new ViewModelTests.FakeHardwareProfileService());
        vm.Target.RefreshAsync().GetAwaiter().GetResult();
        vm.Source.SelectImageAsync(@"D:\Win11.iso").GetAwaiter().GetResult();
        vm.Hardware.RefreshAsync(isVietnamese: true).GetAwaiter().GetResult();

        var window = new MainWindow(vm)
        {
            Width = 1160,
            Height = 820,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        FlushRender(window);

        Assert(window.ActualWidth <= 1160 && window.ActualHeight <= 820, "Window exceeded 1160x820 bounds.");

        // Single-page layout: Source, Target, Options all visible on first load
        Assert(vm.IsInstallMainTab && ((FrameworkElement)window.FindName("MainInstallSection")).ActualHeight > 0, "MainInstallSection did not render.");
        Assert(((FrameworkElement)window.FindName("SourceSection")).ActualHeight > 0, "SourceSection did not render.");
        Assert(((FrameworkElement)window.FindName("TargetSection")).ActualHeight > 0, "TargetSection did not render.");
        Assert(((FrameworkElement)window.FindName("OptionsSection")).ActualHeight > 0, "OptionsSection did not render.");

        // Source header pills & editable/clipboard controls
        Assert(((FrameworkElement)window.FindName("DownloadSourceButton")).ActualHeight > 0, "DownloadSourceButton did not render.");
        Assert(((FrameworkElement)window.FindName("FindSourceButton")).ActualHeight > 0, "FindSourceButton did not render.");
        Assert(((FrameworkElement)window.FindName("VerifyGenuineButton")).ActualHeight > 0, "VerifyGenuineButton did not render.");
        Assert(((FrameworkElement)window.FindName("VerifyImageToggle")).ActualHeight > 0, "VerifyImageToggle did not render.");
        Assert(((FrameworkElement)window.FindName("ClipboardSourceButton")).ActualHeight > 0, "ClipboardSourceButton did not render.");
        Assert(((FrameworkElement)window.FindName("SourcePathBox")).ActualHeight > 0, "SourcePathBox did not render.");

        // Target disk map + right-side role combos
        var diskMapList = (ItemsControl)window.FindName("DiskMapList");
        Assert(diskMapList.Items.Count == 3, "DiskMapList must show all 3 disks.");
        var peContainer = (FrameworkElement)diskMapList.ItemContainerGenerator.ContainerFromIndex(1);
        var peRowBorder = FindVisualChild<Border>(peContainer)!;
        Assert(!peRowBorder.IsEnabled, "FelixPE disk row must be disabled.");
        Assert(((FrameworkElement)window.FindName("WindowsRoleCombo")).ActualHeight > 0, "WindowsRoleCombo did not render.");
        Assert(((FrameworkElement)window.FindName("SystemRoleCombo")).ActualHeight > 0, "SystemRoleCombo did not render.");
        Assert(((FrameworkElement)window.FindName("RecoveryRoleCombo")).ActualHeight > 0, "RecoveryRoleCombo did not render.");

        // Options pills & controls
        Assert(((FrameworkElement)window.FindName("DriversButton")).ActualHeight > 0, "DriversButton did not render.");
        Assert(((FrameworkElement)window.FindName("RemoteAppsButton")).ActualHeight > 0, "RemoteAppsButton did not render.");
        Assert(((FrameworkElement)window.FindName("RecordButton")).ActualHeight > 0, "RecordButton did not render.");
        Assert(((FrameworkElement)window.FindName("BloatwareButton")).ActualHeight > 0, "BloatwareButton did not render.");
        Assert(((FrameworkElement)window.FindName("RescueUsbCombo")).ActualHeight > 0, "RescueUsbCombo did not render.");
        Assert(((FrameworkElement)window.FindName("MakeRescueUsbButton")).ActualHeight > 0, "MakeRescueUsbButton did not render.");
        Assert(((FrameworkElement)window.FindName("BypassMsAccountCheck")).ActualHeight > 0, "BypassMsAccountCheck did not render.");
        Assert(((FrameworkElement)window.FindName("CreateUnattendButton")).ActualHeight > 0, "CreateUnattendButton did not render.");
        Assert(((FrameworkElement)window.FindName("ClearUnattendButton")).ActualHeight > 0, "ClearUnattendButton did not render.");
        Assert(((FrameworkElement)window.FindName("StartButton")).ActualHeight > 0, "StartButton did not render.");

        vm.Target.SelectPartition(vm.Target.Disks[0].Cards[1]);
        FlushRender(window);
        Assert(((FrameworkElement)window.FindName("TargetSummaryBox")).ActualHeight > 0, "TargetSummaryBox did not appear.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(window, Path.Combine(captureDir, "01-main-light.png"));
        }

        // Dark mode & Erase check
        ThemeService.SetChoice(new ThemeChoice(ColorMode.Dark, ThemeService.Choice.Accent), save: false);
        vm.Target.Mode = InstallTargetMode.EraseDisk;
        vm.Target.SelectDiskForErase(vm.Target.Disks[2]);
        FlushRender(window);
        Assert(vm.Target.Disks[2].HasPlannedLayout && vm.Target.Disks[2].PlannedLayout!.Count == 4, "Erase layout must contain 4 items.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(window, Path.Combine(captureDir, "02-main-erase-dark.png"));
        }

        Assert(((FrameworkElement)window.FindName("FooterVersionText")).ActualHeight > 0, "FooterVersionText did not render.");
        Assert(((FrameworkElement)window.FindName("FooterStatusLabel")).ActualHeight > 0, "FooterStatusLabel did not render.");

        // Hardware Check & Recommendation tab check
        ThemeService.SetChoice(new ThemeChoice(ColorMode.Light, ThemeService.Choice.Accent), save: false);
        vm.ActiveMainTab = MainSectionTab.HardwareCheck;
        FlushRender(window);
        Assert(((FrameworkElement)window.FindName("HardwareSection")).ActualHeight > 0, "HardwareSection did not render.");
        Assert(((ItemsControl)window.FindName("HardwareChecksList")).Items.Count == 6, "HardwareChecksList must list 6 checks.");
        Assert(((ItemsControl)window.FindName("HardwareOptionsList")).Items.Count == 4, "HardwareOptionsList must list 4 edition options.");
        Assert(((FrameworkElement)window.FindName("DownloadRecommendedButton")).ActualHeight > 0, "DownloadRecommendedButton did not render.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(window, Path.Combine(captureDir, "03-hardware-light.png"));
        }

        // Backup tab check
        vm.ActiveMainTab = MainSectionTab.BackupWindows;
        vm.Backup.Method = WindowsBackupMethod.SysprepGeneralizeCapture;
        FlushRender(window);
        Assert(((FrameworkElement)window.FindName("BackupSection")).ActualHeight > 0, "BackupSection did not render.");
        Assert(((FrameworkElement)window.FindName("SysprepOptionsCard")).ActualHeight > 0, "SysprepOptionsCard did not render.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(window, Path.Combine(captureDir, "08-backup-light.png"));
        }

        vm.ActiveMainTab = MainSectionTab.InstallWindows;
        FlushRender(window);

        LanguageService.Apply(AppLanguage.Vietnamese, save: false);
        vm.SetLanguage(true);
        var viStart = app.TryFindResource("WS_Start") as string;
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            vm.ActiveMainTab = MainSectionTab.InstallWindows;
            vm.Target.Mode = InstallTargetMode.ExistingPartition;
            vm.Target.SelectPartition(vm.Target.Disks[0].Cards[1]);
            FlushRender(window);
            CaptureWindow(window, Path.Combine(captureDir, "01b-main-vi-light.png"));

            vm.ActiveMainTab = MainSectionTab.HardwareCheck;
            FlushRender(window);
            CaptureWindow(window, Path.Combine(captureDir, "03b-hardware-vi-light.png"));

            vm.ActiveMainTab = MainSectionTab.InstallWindows;
        }
        LanguageService.Apply(AppLanguage.English, save: false);
        vm.SetLanguage(false);
        var enStart = app.TryFindResource("WS_Start") as string;
        Assert(!string.IsNullOrWhiteSpace(viStart) && !string.IsNullOrWhiteSpace(enStart) && viStart != enStart,
            "Language toggle did not change WS_Start resource.");

        var findDialog = new FindSourceDialog(new FakeSourceScanner(), new FakeImageInspector())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        findDialog.Show();
        findDialog.UpdateLayout();
        Assert(((ListBox)findDialog.FindName("ResultsList")).ActualWidth > 0, "FindSourceDialog ResultsList did not render.");
        Assert(((FrameworkElement)findDialog.FindName("RescanButton")).ActualHeight > 0, "FindSourceDialog RescanButton did not render.");
        Assert(!((UIElement)findDialog.FindName("UseButton")).IsEnabled, "FindSourceDialog UseButton must start disabled when nothing is selected.");
        findDialog.Close();

        LanguageService.Apply(AppLanguage.Vietnamese, save: false);
        var populatedFind = new FindSourceDialog(new PopulatedSourceScanner(), new FakeImageInspector())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        populatedFind.Show();
        populatedFind.ViewModel.StartAsync().GetAwaiter().GetResult();
        FlushRender(populatedFind);
        var resultsList = (ListBox)populatedFind.FindName("ResultsList");
        Assert(resultsList.Items.Count == 2, "Populated FindSourceDialog must list 2 items.");
        Assert(((UIElement)populatedFind.FindName("UseButton")).IsEnabled, "Populated FindSourceDialog UseButton must be enabled.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(populatedFind, Path.Combine(captureDir, "05-find-source-vi.png"));
        }
        populatedFind.Close();
        LanguageService.Apply(AppLanguage.English, save: false);

        var reviewVm = new ReviewViewModel(vm.CreateRequest(), vm.Target.Summary!);
        var reviewDialog = new ReviewDialog(reviewVm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        reviewDialog.Show();
        reviewDialog.UpdateLayout();
        Assert(((FrameworkElement)reviewDialog.FindName("ConfirmButton")).ActualHeight > 0, "ReviewDialog ConfirmButton did not render.");
        reviewDialog.Close();

        var unattendDialog = new UnattendDialog
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        unattendDialog.Show();
        unattendDialog.UpdateLayout();
        var previewBox = (TextBox)unattendDialog.FindName("PreviewBox");
        Assert(previewBox.Text.Contains("<unattend", StringComparison.OrdinalIgnoreCase), "UnattendDialog must generate <unattend> XML.");
        Assert(((FrameworkElement)unattendDialog.FindName("UseButton")).ActualHeight > 0, "UnattendDialog UseButton did not render.");
        unattendDialog.Close();

        var driverVm = new DriverSelectionViewModel(
            new ViewModelTests.FakeDriverStoreService(),
            DriverBackupPreset.Minimum);
        var driverDialog = new DriverDialog(driverVm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        driverDialog.Show();
        driverVm.LoadAsync().GetAwaiter().GetResult();
        FlushRender(driverDialog);
        Assert(((DataGrid)driverDialog.FindName("DriversGrid")).Columns.Count == 7, "DriverDialog must have 7 columns.");
        Assert(((FrameworkElement)driverDialog.FindName("ApplyDriversButton")).ActualHeight > 0, "DriverDialog ApplyDriversButton did not render.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(driverDialog, Path.Combine(captureDir, "06-driver-dialog.png"));
        }
        driverDialog.Close();

        var remoteVm = new RemoteAppsViewModel([new RemoteAppSelection(RemoteSupportAppKind.AnyDesk, true, "FLX123456789")]);
        var remoteDialog = new RemoteAppsDialog(remoteVm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        remoteDialog.Show();
        FlushRender(remoteDialog);
        Assert(((ItemsControl)remoteDialog.FindName("RemoteAppsList")).Items.Count == 4, "RemoteAppsDialog must render 4 remote apps.");
        Assert(((FrameworkElement)remoteDialog.FindName("ApplyRemoteAppsButton")).ActualHeight > 0, "RemoteAppsDialog Apply button did not render.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(remoteDialog, Path.Combine(captureDir, "07-remote-dialog.png"));
        }
        remoteDialog.Close();

        var preparedSample = new PreparedDesktopHandoff(
            @"D:\FelixWinDeploy",
            @"D:\FelixWinDeploy\session.json",
            'D',
            "{11111111-1111-1111-1111-111111111111}",
            "{22222222-2222-2222-2222-222222222222}",
            new DesktopHandoffSession(
                SchemaVersion: 1,
                SessionId: "sample-session",
                CreatedUtc: DateTime.UtcNow,
                Request: vm.CreateRequest(),
                BcdOsId: "{11111111-1111-1111-1111-111111111111}",
                BcdDeviceId: "{22222222-2222-2222-2222-222222222222}"));
        var handoffDialog = new HandoffReadyDialog(preparedSample)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        handoffDialog.Show();
        FlushRender(handoffDialog);
        Assert(((FrameworkElement)handoffDialog.FindName("RestartNowButton")).ActualHeight > 0, "HandoffReadyDialog RestartNowButton did not render.");
        Assert(((FrameworkElement)handoffDialog.FindName("NextRestartButton")).ActualHeight > 0, "HandoffReadyDialog NextRestartButton did not render.");
        Assert(((FrameworkElement)handoffDialog.FindName("CancelPreparationButton")).ActualHeight > 0, "HandoffReadyDialog CancelPreparationButton did not render.");
        handoffDialog.Close();

        var bloatDialog = new BloatwareDialog(true, true, ["Microsoft.BingNews"])
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        bloatDialog.Show();
        FlushRender(bloatDialog);
        Assert(((FrameworkElement)bloatDialog.FindName("ApplyBloatwareButton")).ActualHeight > 0, "BloatwareDialog ApplyBloatwareButton did not render.");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            CaptureWindow(bloatDialog, Path.Combine(captureDir, "04-bloatware-dialog.png"));
        }
        bloatDialog.Close();

        var recDialog = new RecordingDialog(new WinPeRecordingOptions(WinPeRecordMode.Both, 30))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        recDialog.Show();
        FlushRender(recDialog);
        Assert(((FrameworkElement)recDialog.FindName("ApplyRecordButton")).ActualHeight > 0, "RecordingDialog ApplyRecordButton did not render.");
        recDialog.Close();

        var resumeWin = new ResumeInstallWindow("")
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        resumeWin.Show();
        FlushRender(resumeWin);
        Assert(((FrameworkElement)resumeWin.FindName("SafeStopButton")).ActualHeight > 0, "ResumeInstallWindow SafeStopButton did not render.");
        Assert(((FrameworkElement)resumeWin.FindName("OpenCmdButton")).ActualHeight > 0, "ResumeInstallWindow OpenCmdButton did not render.");
        Assert(((FrameworkElement)resumeWin.FindName("LogConsoleBox")).ActualHeight > 0, "ResumeInstallWindow LogConsoleBox did not render.");
        resumeWin.Close();

        var resumeBakWin = new ResumeBackupWindow("")
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        resumeBakWin.Show();
        FlushRender(resumeBakWin);
        Assert(((FrameworkElement)resumeBakWin.FindName("OpenCmdButton")).ActualHeight > 0, "ResumeBackupWindow OpenCmdButton did not render.");
        Assert(((FrameworkElement)resumeBakWin.FindName("LogConsoleBox")).ActualHeight > 0, "ResumeBackupWindow LogConsoleBox did not render.");
        resumeBakWin.Close();

        window.Close();
        Console.WriteLine("PASS LayoutProbe");
    }

    private static void FlushRender(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void CaptureWindow(Window window, string filePath)
    {
        var visual = VisualTreeHelper.GetChildrenCount(window) > 0
            ? VisualTreeHelper.GetChild(window, 0) as Visual ?? window
            : window;
        var fe = visual as FrameworkElement ?? window;
        var width = Math.Max(1, (int)Math.Ceiling(fe.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(fe.ActualHeight));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private static TargetDiskFacts GptWindowsDisk(int number) =>
        new(number, "id0", "s0", "Samsung SSD 980", "NVMe", "GPT", 256L << 30,
            false, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 260L << 20, "", "EFI", "FAT32", "g1", "s1", true, false, false, false, false, false, false),
                new(2, 1L << 30, 120L << 30, "C", "Windows", "NTFS", "g2", "s2", false, false, false, false, false, false, true, 60L << 30),
                new(3, 122L << 30, 1L << 30, "", "Recovery", "NTFS", "g3", "s3", false, false, true, false, false, false, false)
            ]);

    private static TargetDiskFacts PeUsbDisk(int number) =>
        new(number, "id1", "s1", "Kingston USB", "USB", "GPT", 32L << 30,
            true, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 31L << 30, "Y", "FELIXPE", "FAT32", "g4", "s4", false, false, false, false, false, false, false, 15L << 30)
            ]);

    private static TargetDiskFacts MbrDataDisk(int number) =>
        new(number, "id2", "s2", "Secondary HDD", "SATA", "MBR", 120L << 30,
            false, false, false, false, false, true,
            [
                new(1, 1024 * 1024, 118L << 30, "F", "Data", "NTFS", "", "s5", false, false, false, false, false, false, false, 90L << 30)
            ]);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeTargetSource(IReadOnlyList<TargetDiskFacts> disks) : ITargetSource
    {
        public Task<IReadOnlyList<TargetDiskFacts>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(disks);
    }

    private sealed class FakeImageInspector : IImageInspector
    {
        public Task<WindowsImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsImageInspection(path,
            [
                new WindowsImageEdition(6, "Windows 11 Pro", "x64", "10.0.26100", 1)
            ]));
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

    private sealed class PopulatedSourceScanner : ISourceScanner
    {
        public async IAsyncEnumerable<SourceCandidate> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new SourceCandidate(
                @"C:\Users\Admin\Downloads\Compressed\en-us_windows_11_business_editions_version_24h2_x64.iso",
                SourceImageKind.Iso,
                9021128704);
            yield return new SourceCandidate(
                @"D:\ISO\vi-vn_windows_10_iot_enterprise_ltsc_2021_x64.iso",
                SourceImageKind.Iso,
                4831838208);
        }
    }
}

internal static class WpfTestHost
{
    public static Application EnsureApp()
    {
        if (Application.Current is not null)
        {
            return Application.Current;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Felix.UI;component/Themes/Colors/Fallback.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Felix.UI;component/Themes/Controls.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Felix.UI;component/Resources/Icons/Fluent/FluentIcons.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FelixWinDeploy;component/Resources/Lang/en/Strings.en.xaml")
        });

        var store = new MemoryPreferenceStore();
        ThemeService.Initialize("FelixWinDeployTests", store);
        ThemeService.LoadOrDefault();
        LanguageService.Initialize("FelixWinDeployTests", store, "FelixWinDeploy");
        LanguageService.Apply(AppLanguage.English, save: false);
        return app;
    }

    private sealed class MemoryPreferenceStore : IPreferenceStore
    {
        private readonly Dictionary<(string Kind, string AppId), string> values = [];
        public string? Read(string kind, string appId) => values.GetValueOrDefault((kind, appId));
        public void Write(string kind, string appId, string value) => values[(kind, appId)] = value;
    }
}
