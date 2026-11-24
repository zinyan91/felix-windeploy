using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Felix.Partition.Services.Native;
using Felix.UI.Controls;
using Felix.UI.Services;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;
using Felix.WindowSetup.ViewModels;
using Microsoft.Win32;

namespace Felix.WindowSetup.Views;

public partial class MainWindow : FeatureWindow
{
    private readonly IBitLockerService bitLockerService = new NativeBitLockerService();

    public MainWindow() : this(new WindowSetupViewModel())
    {
    }

    public MainWindow(WindowSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        UpdateThemeButtonLabel();
        ThemeService.ThemeChanged += _ => UpdateThemeButtonLabel();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public WindowSetupViewModel ViewModel { get; }

    private void NavInstall_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ActiveMainTab = MainSectionTab.InstallWindows;

    private void NavBackup_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ActiveMainTab = MainSectionTab.BackupWindows;

    private void NavHardware_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ActiveMainTab = MainSectionTab.HardwareCheck;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var isVietnamese = LanguageService.Current == AppLanguage.Vietnamese;
        ViewModel.SetLanguage(isVietnamese);
        await ReloadTargetsAsync();
        ViewModel.Target.TryAutoSelectDefaultTarget();
        try
        {
            await Task.WhenAll(
                ViewModel.Backup.RefreshSourcesAsync(),
                ViewModel.Hardware.RefreshAsync(isVietnamese));
        }
        catch
        {
        }
    }

    private async Task ReloadTargetsAsync()
    {
        try
        {
            await ViewModel.Target.RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        var msg = TryFindResource("WS_ClosingBlocked") as string
            ?? "Felix WinDeploy cannot close while disk preparation or installation is running.";
        var caption = TryFindResource("WS_ClosingBlockedTitle") as string ?? "Installation in progress";
        MessageBox.Show(this, msg, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ToggleLanguage_Click(object sender, RoutedEventArgs e)
    {
        var next = LanguageService.Current == AppLanguage.Vietnamese
            ? AppLanguage.English
            : AppLanguage.Vietnamese;
        LanguageService.Apply(next, save: true);
        ViewModel.SetLanguage(next == AppLanguage.Vietnamese);
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var nextMode = ThemeService.IsDark ? ColorMode.Light : ColorMode.Dark;
        ThemeService.SetChoice(new ThemeChoice(nextMode, ThemeService.Choice.Accent), save: true);
        UpdateThemeButtonLabel();
    }

    private void UpdateThemeButtonLabel()
    {
        var key = ThemeService.IsDark ? "WS_ThemeLight" : "WS_ThemeDark";
        ThemeButtonText.SetResourceReference(TextBlock.TextProperty, key);
    }

    private async void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Windows images (*.iso;*.wim;*.esd;*.swm)|*.iso;*.wim;*.esd;*.swm|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ViewModel.Source.SelectImageAsync(dialog.FileName);
        }
    }

    private async void FindSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FindSourceDialog(ViewModel.SourceScanner, ViewModel.ImageInspector) { Owner = this };
        dialog.ShowDialog();
        if (!string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            await ViewModel.Source.SelectImageAsync(dialog.SelectedPath);
        }
    }

    private void DownloadSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SourceDownloadCatalog.OfficialDownloadUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            try { Clipboard.SetText(SourceDownloadCatalog.OfficialDownloadUrl); } catch { }
            MessageBox.Show(this, SourceDownloadCatalog.OfficialDownloadUrl, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void RefreshHardware_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.Hardware.RefreshAsync(LanguageService.Current == AppLanguage.Vietnamese);
    }

    private void VerifyGenuine_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Source.ImagePath))
        {
            return;
        }

        var dialog = new IntegrityCheckDialog(ViewModel.Source.ImagePath) { Owner = this };
        dialog.ShowDialog();
    }

    private async void PasteClipboardSource_Click(object sender, RoutedEventArgs e)
    {
        string? candidate = null;
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
            {
                candidate = files[0];
            }
        }
        else if (Clipboard.ContainsText())
        {
            candidate = Clipboard.GetText();
        }

        if (!await ViewModel.Source.PasteClipboardTextAsync(candidate))
        {
            var msg = TryFindResource("WS_Clipboard_Invalid") as string
                ?? "Clipboard does not contain a valid ISO, WIM, ESD, or SWM path.";
            MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SourcePathBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void SourcePathBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            await ViewModel.Source.SelectImageAsync(files[0]);
        }
    }

    private async void SourcePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ViewModel.Source.CommitEditablePathAsync();
        }
    }

    private async void SourcePathBox_LostFocus(object sender, RoutedEventArgs e) =>
        await ViewModel.Source.CommitEditablePathAsync();

    private void CreateUnattend_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UnattendDialog(ViewModel.GeneratedUnattendSpec) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ResultSpec is { } spec)
        {
            ViewModel.ApplyGeneratedUnattend(spec, dialog.ResultPath == null || !dialog.ResultPath.Contains("WindowSetupUnattend") ? dialog.ResultPath : null);
        }
        else if (!string.IsNullOrWhiteSpace(dialog.ResultPath))
        {
            ViewModel.AnswerPath = dialog.ResultPath;
        }
    }

    private void BrowseAnswerFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.AnswerPath = dialog.FileName;
        }
    }

    private void ClearUnattend_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ClearUnattend();

    private void ConfigureDrivers_Click(object sender, RoutedEventArgs e)
    {
        var dialogVm = new DriverSelectionViewModel(
            ViewModel.DriverStore,
            ViewModel.DriverBackupPreset,
            ViewModel.SelectedDriverPublishedNames,
            initialKeepLatestOnly: true,
            initialBackupPrinters: ViewModel.BackupPrinters,
            isPrintBrmAvailable: ViewModel.IsPrintBrmAvailable);
        var dialog = new DriverDialog(dialogVm) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Confirmed)
        {
            ViewModel.ApplyDriverSelection(
                dialogVm.ActivePreset,
                dialogVm.GetSelectedPublishedNames());
            ViewModel.BackupPrinters = dialogVm.BackupPrinters;
        }
    }

    private void ConfigureBloatware_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BloatwareDialog(
            ViewModel.IsBloatwareEnabled,
            ViewModel.BlockPromotedApps,
            ViewModel.SelectedBloatwarePrefixes,
            ViewModel.Source.ImagePath,
            ViewModel.Source.Edition?.Index ?? 0)
        {
            Owner = this
        };
        dialog.ShowDialog();
        if (dialog.Confirmed)
        {
            ViewModel.ApplyBloatwareSelection(
                dialog.ResultEnabled,
                dialog.ResultBlockPromoted,
                dialog.ResultSelectedPrefixes);
        }
    }

    private void BrowseWindowsDrivers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.WindowsDriversFolder = dialog.FolderName;
        }
    }

    private void BrowseWinPeDrivers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.WinPeDriversFolder = dialog.FolderName;
        }
    }

    private void BrowseExtraWinPe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Windows PE images (*.wim)|*.wim|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.ExtraWinPePath = dialog.FileName;
        }
    }

    private async void FindWinPe_Click(object sender, RoutedEventArgs e)
    {
        FindWinPeButton.IsEnabled = false;
        try
        {
            var disks = ViewModel.Target.Disks.Select(d => d.Facts).ToList();
            var found = await ViewModel.ExtraBootEntries.FindWinPeWimsAsync(disks);
            if (found.Count == 0)
            {
                var msg = TryFindResource("WS_ExtraWinPe_NoneFound") as string
                    ?? "No WinPE .wim image was found on internal drives.";
                MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ViewModel.ExtraWinPePath = found[0];
        }
        finally
        {
            FindWinPeButton.IsEnabled = true;
        }
    }

    private void ConfigureRemoteApps_Click(object sender, RoutedEventArgs e)
    {
        var dialogVm = new RemoteAppsViewModel(ViewModel.RemoteApps);
        var dialog = new RemoteAppsDialog(dialogVm) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Confirmed)
        {
            ViewModel.ApplyRemoteApps(dialogVm.BuildSelections());
        }
    }

    private void ConfigureRecording_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RecordingDialog(ViewModel.RecordingOptions) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Confirmed)
        {
            ViewModel.ApplyRecordingOptions(dialog.ResultOptions);
        }
    }

    private async void MakeRescueUsb_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRescueUsbOption is not { DiskNumber: >= 0, IsEligible: true } candidate)
        {
            return;
        }

        var dialog = new MakeRescueUsbDialog(candidate, ViewModel.Source.ImagePath) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Succeeded)
        {
            await ReloadTargetsAsync();
        }
    }

    private void BrowseFirstDesktop_Click(object sender, RoutedEventArgs e)
    {
        var filter = ViewModel.IsFirstDesktopZipMode
            ? "ZIP archives (*.zip)|*.zip|All files (*.*)|*.*"
            : "Executable files (*.exe)|*.exe|All files (*.*)|*.*";
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.FirstDesktopSourcePath = dialog.FileName;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            await ReloadTargetsAsync();
        }
    }

    private void DiskCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DiskRowViewModel row } && ViewModel.Target.IsEraseMode)
        {
            ViewModel.Target.SelectDiskForErase(row);
        }
    }

    private void PartitionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PartitionCardViewModel card })
        {
            if (ViewModel.Target.IsEraseMode)
            {
                ViewModel.Target.Mode = InstallTargetMode.ExistingPartition;
            }
            ViewModel.Target.SelectPartition(card);
        }
    }

    private static PartitionCardViewModel? CardFromMenuSender(object sender) =>
        sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: PartitionCardViewModel card } } }
            ? card
            : null;

    private void ContextAssignWindows_Click(object sender, RoutedEventArgs e)
    {
        if (CardFromMenuSender(sender) is { } card)
        {
            ViewModel.Target.AssignRole(card, PartitionRole.Windows);
        }
    }

    private void ContextAssignSystem_Click(object sender, RoutedEventArgs e)
    {
        if (CardFromMenuSender(sender) is { } card)
        {
            ViewModel.Target.AssignRole(card, PartitionRole.System);
        }
    }

    private void ContextAssignRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (CardFromMenuSender(sender) is { } card)
        {
            ViewModel.Target.AssignRole(card, PartitionRole.Recovery);
        }
    }

    private async void ContextUnlockBitLocker_Click(object sender, RoutedEventArgs e)
    {
        if (CardFromMenuSender(sender) is not { } card || string.IsNullOrWhiteSpace(card.Facts.Letter))
        {
            return;
        }

        var dialog = new BitLockerUnlockDialog(card.Facts.Letter[0], card.Facts.Label, bitLockerService) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Unlocked)
        {
            await ReloadTargetsAsync();
        }
    }

    private async void ContextTurnOffBitLocker_Click(object sender, RoutedEventArgs e)
    {
        if (CardFromMenuSender(sender) is not { } card || string.IsNullOrWhiteSpace(card.Facts.Letter))
        {
            return;
        }

        var res = await bitLockerService.TurnOffEncryptionAsync(card.Facts.Letter[0]);
        MessageBox.Show(
            this,
            res.Output,
            Title,
            MessageBoxButton.OK,
            res.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Error);
        if (res.Succeeded)
        {
            await ReloadTargetsAsync();
        }
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanStart)
        {
            return;
        }

        var validation = ViewModel.Validate();
        if (!validation.CanProceed)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, validation.Issues.Where(issue => issue.IsBlocking).Select(issue => issue.Message)),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var request = ViewModel.CreateRequest();
        var summary = ViewModel.Target.Summary!;
        var rescueUsb = ViewModel.SelectedRescueUsbOption is { DiskNumber: >= 0 } u ? u : null;
        var reviewVm = new ReviewViewModel(request, summary, ViewModel.ExecutionMode, rescueUsb);
        var review = new ReviewDialog(reviewVm) { Owner = this };
        review.ShowDialog();
        if (!review.Confirmed)
        {
            return;
        }

        try
        {
            if (ViewModel.ExecutionMode == SetupExecutionMode.DesktopHandoff)
            {
                var handoff = await ViewModel.PrepareDesktopHandoffAsync();
                var readyDialog = new HandoffReadyDialog(handoff) { Owner = this };
                readyDialog.ShowDialog();

                if (readyDialog.SelectedAction == HandoffReadyAction.CancelPreparation)
                {
                    await ViewModel.DesktopHandoff.CancelPreparedHandoffAsync(handoff);
                    return;
                }

                if (readyDialog.SelectedAction == HandoffReadyAction.RestartNow)
                {
                    var shutdown = SystemTools.Find("shutdown.exe");
                    if (!string.IsNullOrEmpty(shutdown))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = shutdown,
                            Arguments = "/r /t 5",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                }
                return;
            }

            var outcome = await ViewModel.InstallAsync();
            if (outcome.Succeeded && ViewModel.RestartWhenComplete)
            {
                var shutdown = SystemTools.Find("shutdown.exe");
                if (!string.IsNullOrEmpty(shutdown))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = shutdown,
                        Arguments = "/r /t 5",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            var logPath = outcome.ExportedLogDirectory ?? outcome.RunDirectory;
            MessageBox.Show(
                this,
                outcome.Succeeded
                    ? $"{logPath}"
                    : $"{outcome.Error}{Environment.NewLine}{Environment.NewLine}{logPath}",
                Title,
                MessageBoxButton.OK,
                outcome.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshBackupSources_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.Backup.RefreshSourcesAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseBackupDestination_Click(object sender, RoutedEventArgs e)
    {
        var filter = ViewModel.Backup.Format switch
        {
            BackupArchiveFormat.Esd => "Windows ESD image (*.esd)|*.esd",
            BackupArchiveFormat.Swm => "Split WIM image (*.swm)|*.swm",
            _ => "Windows WIM image (*.wim)|*.wim"
        };
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ViewModel.Backup.Format switch
            {
                BackupArchiveFormat.Esd => ".esd",
                BackupArchiveFormat.Swm => ".swm",
                _ => ".wim"
            },
            FileName = string.IsNullOrWhiteSpace(ViewModel.Backup.OutputPath)
                ? "WindowsBackup"
                : System.IO.Path.GetFileName(ViewModel.Backup.OutputPath)
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Backup.OutputPath = dialog.FileName;
        }
    }

    private void BrowseSysprepUnattend_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "XML answer files (*.xml)|*.xml|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Backup.SysprepUnattendPath = dialog.FileName;
        }
    }

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Backup.RefreshPreflight();
        if (!ViewModel.Backup.CanStartBackup)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.Backup.PreflightErrorText))
            {
                MessageBox.Show(this, ViewModel.Backup.PreflightErrorText, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        try
        {
            var (directCompleted, outFile, session) = await ViewModel.Backup.RunBackupAsync();
            if (directCompleted)
            {
                if (ViewModel.Backup.ShutdownAfterCapture)
                {
                    var shutdown = SystemTools.Find("shutdown.exe");
                    if (!string.IsNullOrEmpty(shutdown))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = shutdown,
                            Arguments = "/s /t 5",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                }
                else
                {
                    MessageBox.Show(this, outFile ?? "Backup completed.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            if (session is not null && ViewModel.Backup.Method == WindowsBackupMethod.WinPeOfflineCapture)
            {
                var confirm = MessageBox.Show(
                    this,
                    $"WinPE backup session ready ({session.StagingDriveLetter}:). Restart now into FelixPE?",
                    Title,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    var shutdown = SystemTools.Find("shutdown.exe");
                    if (!string.IsNullOrEmpty(shutdown))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = shutdown,
                            Arguments = "/r /t 5",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
