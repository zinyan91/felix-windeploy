using System.Diagnostics;
using System.IO;
using System.Windows;
using Felix.Partition.Services.Native;
using Felix.UI.Controls;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.Views;

public partial class ResumeInstallWindow : FeatureWindow
{
    private readonly string sessionFilePath;
    private DesktopHandoffSession? loadedSession;
    private InstallExecutionControl? executionControl;
    private TaskCompletionSource<SafeStopDecision>? safeStopPauseTcs;
    private bool started;
    private double logFontSize = 12.5;

    public ResumeInstallWindow(string sessionFilePath)
    {
        this.sessionFilePath = sessionFilePath;
        InitializeComponent();
        Loaded += ResumeInstallWindow_Loaded;
    }

    private async void ResumeInstallWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (started)
        {
            return;
        }
        started = true;
        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
        {
            StageText.Text = "Ready (no active session file)";
            return;
        }

        await RunInstallAttemptAsync();
    }

    private async Task RunInstallAttemptAsync()
    {
        RetryButton.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Collapsed;
        CancelToOldWindowsButton.Visibility = Visibility.Collapsed;
        SafeStopButton.IsEnabled = true;
        ErrorText.Text = "";

        WinPeScreenRecorder? recorder = null;
        try
        {
            var json = await File.ReadAllTextAsync(sessionFilePath);
            var session = DesktopHandoffSession.FromJson(json);
            loadedSession = session;
            var sessionDir = Path.GetDirectoryName(Path.GetFullPath(sessionFilePath))!;

            var resolvedImagePath = NativeDesktopHandoffService.ResolveResumeSourcePath(session, sessionDir);
            var (stagedDrivers, stagedFirstBoot) = NativeDesktopHandoffService.RehydrateStagedArtifacts(session, sessionDir);

            var request = session.Request;
            if (!string.Equals(resolvedImagePath, request.Source.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                var inspection = WindowSetupInputFiles.Inspect(
                    resolvedImagePath,
                    request.AutounattendPath,
                    request.Options?.Drivers?.WindowsDriversFolder,
                    request.Options?.Drivers?.WinPeDriversFolder,
                    request.Options?.BootAndRecovery?.ExtraWinPePath,
                    request.Options?.FirstBoot?.FirstDesktopApp)
                    with
                    {
                        ExtraWinPeOnTarget = request.FileInspection?.ExtraWinPeOnTarget == true,
                        ExtraWinPeIsRemovable = request.FileInspection?.ExtraWinPeIsRemovable == true,
                        ExtraWinPeIsBitLocker = request.FileInspection?.ExtraWinPeIsBitLocker == true
                    };

                request = request with
                {
                    Source = request.Source with { ImagePath = resolvedImagePath },
                    FileInspection = inspection
                };
            }

            SourceSummaryText.Text = $"{request.Source.Edition} ({Path.GetFileName(request.Source.ImagePath)})";
            TargetSummaryText.Text = request.Target.Mode == InstallTargetMode.EraseDisk
                ? $"Disk {request.Target.DiskNumber} · {request.Target.DiskModel}".Trim(' ', '·')
                : $"Disk {request.Target.DiskNumber} · {request.Target.WindowsPartition.DisplayName}";

            var recOptions = session.Recording ?? request.Options?.Recording;
            if (recOptions is { IsActive: true })
            {
                recorder = new WinPeScreenRecorder(recOptions, sessionDir);
                recorder.Start();
                AppendLog($"Screen recording started ({recOptions.Mode}).");
            }

            executionControl = new InstallExecutionControl
            {
                OnSafeStopBoundaryAsync = HandleSafeStopBoundaryAsync
            };

            var platform = new WindowSetupInstallationPlatform(
                WindowSetupEngineLocator.Find(),
                new NativeDriverStoreService(),
                new NativeNetworkAndPrinterBackupService(),
                new NativeExtraBootEntriesService(),
                new NativeFirstBootStagingService(),
                stagedDrivers,
                stagedFirstBoot);
            var runner = new WindowInstallationRunner(platform);
            var progress = new Progress<InstallProgressUpdate>(update =>
            {
                InstallProgressBar.Value = update.Percent;
                PercentText.Text = $"{update.Percent}%";
                StageText.Text = update.Message;
                AppendLog($"[{update.Stage}/{update.State}] {update.Message}");
            });

            var journalRoot = Path.Combine(Path.GetTempPath(), "FelixPE", "WindowSetup");
            var outcome = await runner.RunAsync(request, journalRoot, progress, CancellationToken.None, executionControl);
            if (!outcome.Succeeded)
            {
                var err = outcome.Error ?? "Installation failed.";
                ErrorText.Text = err;
                AppendLog($"[Error] {err}");
                RetryButton.Visibility = Visibility.Visible;
                SafeStopButton.IsEnabled = false;
                if (recorder is not null)
                {
                    await recorder.StopAndExportAsync(null);
                }
                return;
            }

            var windowsLetter = !string.IsNullOrWhiteSpace(request.Target.WindowsPartition.AccessPath)
                ? char.ToUpperInvariant(request.Target.WindowsPartition.AccessPath[0])
                : (char?)null;
            if (recorder is not null)
            {
                var exportedRec = await recorder.StopAndExportAsync(windowsLetter);
                AppendLog($"Recording exported to: {exportedRec}");
            }

            var handoffService = new NativeDesktopHandoffService();
            var systemLetter = !string.IsNullOrWhiteSpace(request.Target.EfiSystemPartition.AccessPath)
                ? char.ToUpperInvariant(request.Target.EfiSystemPartition.AccessPath[0])
                : '\0';
            await handoffService.CompleteWinPeResumeCleanupAsync(
                session,
                sessionFilePath,
                systemLetter,
                request.Target.Firmware);

            InstallProgressBar.Value = 100;
            PercentText.Text = "100%";
            StageText.Text = TryFindResource("WS_Resume_Rebooting") as string ?? "Installation completed. Restarting...";
            AppendLog("Setup completed successfully.");
            SafeStopButton.IsEnabled = false;
            await Task.Delay(1500);
            TriggerPowerAction(reboot: true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            AppendLog($"[Error] {ex.Message}");
            RetryButton.Visibility = Visibility.Visible;
            SafeStopButton.IsEnabled = false;
        }
        finally
        {
            if (recorder is not null)
            {
                await recorder.DisposeAsync();
            }
        }
    }

    private async Task<SafeStopDecision> HandleSafeStopBoundaryAsync(InstallRunStage nextStage, bool writeStarted)
    {
        safeStopPauseTcs = new TaskCompletionSource<SafeStopDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            StageText.Text = $"Paused before {nextStage}";
            AppendLog($"[SafeStop] Paused before {nextStage} (writeStarted={writeStarted}).");
            ContinueButton.Visibility = Visibility.Visible;
            CancelToOldWindowsButton.Visibility = writeStarted ? Visibility.Collapsed : Visibility.Visible;
            SafeStopButton.IsEnabled = false;
        });

        var decision = await safeStopPauseTcs.Task;
        Dispatcher.Invoke(() =>
        {
            ContinueButton.Visibility = Visibility.Collapsed;
            CancelToOldWindowsButton.Visibility = Visibility.Collapsed;
            SafeStopButton.IsEnabled = decision == SafeStopDecision.Continue;
        });
        return decision;
    }

    private void AppendLog(string line)
    {
        LogConsoleBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogConsoleBox.ScrollToEnd();
    }

    private void FontDecrease_Click(object sender, RoutedEventArgs e)
    {
        logFontSize = Math.Max(10, logFontSize - 1);
        LogConsoleBox.FontSize = logFontSize;
    }

    private void FontIncrease_Click(object sender, RoutedEventArgs e)
    {
        logFontSize = Math.Min(20, logFontSize + 1);
        LogConsoleBox.FontSize = logFontSize;
    }

    private void SafeStop_Click(object sender, RoutedEventArgs e)
    {
        executionControl?.RequestSafeStop();
        AppendLog("Safe stop requested. Waiting for current step boundary...");
        SafeStopButton.IsEnabled = false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) =>
        safeStopPauseTcs?.TrySetResult(SafeStopDecision.Continue);

    private async void CancelToOldWindows_Click(object sender, RoutedEventArgs e)
    {
        safeStopPauseTcs?.TrySetResult(SafeStopDecision.AbortInstall);
        if (loadedSession is not null)
        {
            try
            {
                var handoffService = new NativeDesktopHandoffService();
                await handoffService.RollbackToOldWindowsFromWinPeAsync(loadedSession, sessionFilePath);
                AppendLog("Rolled back BCD & staging files. Ready to restart into previous Windows.");
                TriggerPowerAction(reboot: true);
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
            }
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) =>
        await RunInstallAttemptAsync();

    private void OpenCmd_Click(object sender, RoutedEventArgs e)
    {
        var cmd = SystemTools.Find("cmd.exe") ?? "cmd.exe";
        Process.Start(new ProcessStartInfo { FileName = cmd, UseShellExecute = true });
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => TriggerPowerAction(reboot: true);

    private void ShutDown_Click(object sender, RoutedEventArgs e) => TriggerPowerAction(reboot: false);

    private static void TriggerPowerAction(bool reboot)
    {
        var shutdown = SystemTools.Find("shutdown.exe");
        if (!string.IsNullOrEmpty(shutdown))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = shutdown,
                Arguments = reboot ? "/r /t 2" : "/s /t 2",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return;
        }

        var wpeutil = SystemTools.Find("wpeutil.exe");
        if (!string.IsNullOrEmpty(wpeutil))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wpeutil,
                Arguments = reboot ? "reboot" : "shutdown",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }
}
