using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Felix.Partition.Services.Native;
using Felix.UI.Controls;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;
using Microsoft.Win32;

namespace Felix.WindowSetup.Views;

public partial class ResumeBackupWindow : FeatureWindow
{
    private readonly string sessionFilePath;
    private WindowsBackupSession? session;
    private string? resolvedSourceRoot;
    private DispatcherTimer? failureCountdownTimer;
    private int secondsRemaining;
    private bool started;
    private double logFontSize = 12.5;

    public ResumeBackupWindow(string sessionFilePath)
    {
        this.sessionFilePath = sessionFilePath;
        InitializeComponent();
        Loaded += ResumeBackupWindow_Loaded;
    }

    private async void ResumeBackupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (started)
        {
            return;
        }
        started = true;

        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
        {
            StageText.Text = "Ready (no backup session file)";
            OpenCmdButton.IsEnabled = true;
            return;
        }

        await ExecuteBackupSessionAsync();
    }

    private async Task ExecuteBackupSessionAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(sessionFilePath);
            session = JsonSerializer.Deserialize<WindowsBackupSession>(json)
                ?? throw new InvalidOperationException("Failed to deserialize backup-session.json.");

            SourceSummaryText.Text = session.SourceRootHint;
            TargetSummaryText.Text = session.OutputPath;

            resolvedSourceRoot = ResolveSourceRoot(session);
            SourceSummaryText.Text = resolvedSourceRoot;

            if (session.Method == WindowsBackupMethod.SysprepGeneralizeCapture)
            {
                AppendLog("Verifying Sysprep execution outcome...");
                var (ok, failureDetail) = await VerifySysprepSucceededAsync(resolvedSourceRoot);
                if (!ok)
                {
                    AppendLog($"Sysprep failure detected: {failureDetail}");
                    AlertText.Text = failureDetail;
                    AlertBanner.Visibility = Visibility.Visible;

                    if (session.Sysprep?.AutoRestoreSafetyBackupOnFailure == true
                        && !string.IsNullOrWhiteSpace(session.SafetyBackupWimPath)
                        && File.Exists(session.SafetyBackupWimPath))
                    {
                        StartFailureRollbackCountdown(session.Sysprep.FailureCountdownSeconds);
                        return;
                    }

                    RestartButton.IsEnabled = true;
                    ShutdownButton.IsEnabled = true;
                    return;
                }
                AppendLog("Sysprep generalization verified.");
            }

            await CaptureFromWinPeAsync(session, resolvedSourceRoot);

            if (session.Method == WindowsBackupMethod.SysprepGeneralizeCapture
                && session.Sysprep?.AutoRestoreSafetyBackupOnSuccess == true
                && !string.IsNullOrWhiteSpace(session.SafetyBackupWimPath)
                && File.Exists(session.SafetyBackupWimPath))
            {
                AppendLog("Restoring pre-Sysprep working Windows from safety backup...");
                await RestoreSafetyWimAsync(session.SafetyBackupWimPath, resolvedSourceRoot);
            }

            await CleanupAfterBackupAsync(session);
            UpdateProgress(100, "Backup completed.");
            RestartButton.IsEnabled = true;
            ShutdownButton.IsEnabled = true;

            if (session.ShutdownAfterCapture && PeEnvironment.IsWinPe)
            {
                AppendLog("Shutting down system as requested...");
                _ = SystemTools.RunAsync("wpeutil.exe", ["shutdown"]);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            AlertText.Text = ex.Message;
            AlertBanner.Visibility = Visibility.Visible;
            RestartButton.IsEnabled = true;
            ShutdownButton.IsEnabled = true;
        }
    }

    private async Task CaptureFromWinPeAsync(WindowsBackupSession currentSession, string sourceRoot)
    {
        var enginePath = WindowSetupEngineLocator.Find();
        var normalizedOut = WindowsBackupMetadata.NormalizeOutputPath(currentSession.OutputPath, currentSession.Format);
        var outDir = Path.GetDirectoryName(normalizedOut)!;
        Directory.CreateDirectory(outDir);

        var tempConfig = Path.Combine(Path.GetTempPath(), $"felix-pe-cap-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(tempConfig, WindowsBackupMetadata.CreateCaptureExclusionConfig());

        var needsTempWim = currentSession.Format == BackupArchiveFormat.Swm;
        var captureTarget = needsTempWim
            ? Path.Combine(outDir, $".felix-tmp-{Guid.NewGuid():N}.wim")
            : normalizedOut;

        try
        {
            if (File.Exists(captureTarget))
            {
                File.Delete(captureTarget);
            }

            UpdateProgress(15, $"Capturing {sourceRoot}...");
            var captureCmd = WimlibCommandBuilder.CreateCapture(
                enginePath,
                sourceRoot,
                captureTarget,
                currentSession.ImageName,
                currentSession.ImageDescription,
                currentSession.Format == BackupArchiveFormat.Swm ? BackupArchiveFormat.Wim : currentSession.Format,
                snapshot: false,
                configFilePath: tempConfig);

            var captureRes = await WimlibProcessRunner.RunAsync(
                captureCmd,
                p =>
                {
                    if (!string.IsNullOrWhiteSpace(p.RawLine))
                    {
                        UpdateProgress(15 + (int)(p.OverallPercent * 0.65), p.RawLine);
                    }
                },
                CancellationToken.None);
            if (captureRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"wimlib capture exited with code {captureRes.ExitCode}.");
            }

            if (currentSession.MakePortableAcrossHardware && currentSession.Method != WindowsBackupMethod.SysprepGeneralizeCapture)
            {
                UpdateProgress(82, "Applying hardware portability patches inside captured SYSTEM hive...");
                var portLogs = await BootPortabilityHelper.PatchCapturedWimPortabilityAsync(enginePath, captureTarget);
                foreach (var entry in portLogs)
                {
                    AppendLog(entry);
                }
            }

            if (currentSession.Format == BackupArchiveFormat.Swm)
            {
                UpdateProgress(88, "Splitting WIM into SWM volumes...");
                var splitCmd = WimlibCommandBuilder.CreateSplit(enginePath, captureTarget, normalizedOut, currentSession.SplitPartSizeMiB);
                var splitRes = await WimlibProcessRunner.RunAsync(splitCmd, p => AppendLog(p.RawLine), CancellationToken.None);
                if (splitRes.ExitCode != 0)
                {
                    throw new InvalidOperationException($"wimlib split exited with code {splitRes.ExitCode}.");
                }
            }
        }
        finally
        {
            if (captureTarget != normalizedOut && File.Exists(captureTarget))
            {
                try { File.Delete(captureTarget); } catch { }
            }
            if (File.Exists(tempConfig))
            {
                try { File.Delete(tempConfig); } catch { }
            }
        }
    }

    private void StartFailureRollbackCountdown(int seconds)
    {
        secondsRemaining = Math.Max(5, seconds);
        RestoreSafetyNowButton.Visibility = Visibility.Visible;
        SkipSafetyRestoreButton.Visibility = Visibility.Visible;
        RestartButton.IsEnabled = true;
        ShutdownButton.IsEnabled = true;

        UpdateCountdownStage();
        failureCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        failureCountdownTimer.Tick += async (_, _) =>
        {
            secondsRemaining--;
            if (secondsRemaining <= 0)
            {
                failureCountdownTimer?.Stop();
                await RunSafetyRestoreNowAsync();
                return;
            }

            UpdateCountdownStage();
        };
        failureCountdownTimer.Start();
    }

    private void UpdateCountdownStage() =>
        StageText.Text = $"Sysprep failed — restoring pre-Sysprep safety backup in {secondsRemaining}s...";

    private async void RestoreSafetyNow_Click(object sender, RoutedEventArgs e)
    {
        failureCountdownTimer?.Stop();
        await RunSafetyRestoreNowAsync();
    }

    private void SkipSafetyRestore_Click(object sender, RoutedEventArgs e)
    {
        failureCountdownTimer?.Stop();
        RestoreSafetyNowButton.Visibility = Visibility.Collapsed;
        SkipSafetyRestoreButton.Visibility = Visibility.Collapsed;
        StageText.Text = "Safety auto-restore cancelled. Open CMD to inspect Panther logs manually.";
        AppendLog("User cancelled automatic safety restore.");
    }

    private async Task RunSafetyRestoreNowAsync()
    {
        RestoreSafetyNowButton.Visibility = Visibility.Collapsed;
        SkipSafetyRestoreButton.Visibility = Visibility.Collapsed;
        if (session is null || string.IsNullOrWhiteSpace(session.SafetyBackupWimPath) || string.IsNullOrWhiteSpace(resolvedSourceRoot))
        {
            return;
        }

        try
        {
            await RestoreSafetyWimAsync(session.SafetyBackupWimPath, resolvedSourceRoot);
            await CleanupAfterBackupAsync(session);
            UpdateProgress(100, "Pre-Sysprep Windows restored safely.");
        }
        catch (Exception ex)
        {
            AppendLog($"Safety restore error: {ex.Message}");
            AlertText.Text = ex.Message;
            AlertBanner.Visibility = Visibility.Visible;
        }
    }

    private async Task RestoreSafetyWimAsync(string safetyWimPath, string targetRoot)
    {
        UpdateProgress(90, "Formatting target OS partition for clean safety restore...");
        var driveLetter = targetRoot.TrimEnd('\\', ':');
        await SystemTools.RunAsync("cmd.exe", ["/c", $"format {driveLetter}: /FS:NTFS /Q /Y"]);

        UpdateProgress(92, "Applying pre-Sysprep safety backup...");
        var enginePath = WindowSetupEngineLocator.Find();
        var applyCmd = WimlibCommandBuilder.CreateApply(enginePath, safetyWimPath, 1, targetRoot);
        var applyRes = await WimlibProcessRunner.RunAsync(applyCmd, p => AppendLog(p.RawLine), CancellationToken.None);
        if (applyRes.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to restore safety backup (wimlib exit {applyRes.ExitCode}).");
        }

        try
        {
            File.Delete(safetyWimPath);
        }
        catch
        {
        }
    }

    private async Task CleanupAfterBackupAsync(WindowsBackupSession currentSession)
    {
        UpdateProgress(97, "Cleaning temporary WinPE boot entry and FELIXWORK...");
        var bcdedit = SystemTools.Find(SystemTools.BcdEdit);
        if (!string.IsNullOrEmpty(bcdedit))
        {
            foreach (var args in DesktopHandoffPlanner.BuildBcdCleanupCommands(currentSession.WindowsBootEntryId, currentSession.RamdiskDeviceId))
            {
                try { await SystemTools.RunAsync(bcdedit, args); } catch { }
            }
        }

        if (File.Exists(sessionFilePath))
        {
            var donePath = Path.Combine(Path.GetDirectoryName(sessionFilePath)!, "backup-session.completed.json");
            try { File.Move(sessionFilePath, donePath, overwrite: true); } catch { }
        }

        if (currentSession.CreatedFelixWork && currentSession.SourceDiskNumber >= 0 && currentSession.SourcePartitionNumber > 0)
        {
            try
            {
                var snap = await Task.Run(NativeStorageReader.Read);
                var disk = snap.Disks.FirstOrDefault(d => d.Number == currentSession.SourceDiskNumber);
                var workPart = disk?.Partitions.FirstOrDefault(p =>
                    !p.Unallocated
                    && (p.Offset == currentSession.FelixWorkOffset
                        || p.Label.Equals("FELIXWORK", StringComparison.OrdinalIgnoreCase)));
                if (disk is not null && workPart is not null)
                {
                    await DiskPart.RunAsync(
                        InstallDiskLayoutBuilder.CreateRemoveFelixWorkAndExtendScript(disk.Number, workPart.Number, currentSession.SourcePartitionNumber),
                        TimeSpan.FromMinutes(2));
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Note: FELIXWORK cleanup skipped ({ex.Message}).");
            }
        }
    }

    private static async Task<(bool Ok, string FailureDetail)> VerifySysprepSucceededAsync(string sourceRoot)
    {
        var setupErr = Path.Combine(sourceRoot, "Windows", "System32", "Sysprep", "Panther", "setuperr.log");
        if (File.Exists(setupErr))
        {
            var content = (await File.ReadAllTextAsync(setupErr)).Trim();
            if (!string.IsNullOrEmpty(content))
            {
                var firstLine = content.Split('\n')[0].Trim();
                return (false, $"Sysprep reported errors in setuperr.log: {firstLine}");
            }
        }

        var softwareHive = Path.Combine(sourceRoot, "Windows", "System32", "config", "SOFTWARE");
        var regExe = SystemTools.Find("reg.exe");
        if (File.Exists(softwareHive) && !string.IsNullOrEmpty(regExe))
        {
            var subKey = $"FelixSyspChk_{Guid.NewGuid():N}";
            var load = await SystemTools.RunAsync(regExe, ["load", $@"HKLM\{subKey}", softwareHive]);
            if (load.ExitCode == 0)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"{subKey}\Microsoft\Windows\CurrentVersion\Setup\State");
                    var imageState = key?.GetValue("ImageState") as string ?? string.Empty;
                    if (!string.IsNullOrEmpty(imageState)
                        && !imageState.Contains("GENERALIZE", StringComparison.OrdinalIgnoreCase)
                        && !imageState.Contains("AUDIT", StringComparison.OrdinalIgnoreCase)
                        && !imageState.Contains("OOBE", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Unexpected post-Sysprep ImageState: {imageState}");
                    }
                }
                finally
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await SystemTools.RunAsync(regExe, ["unload", $@"HKLM\{subKey}"]);
                }
            }
        }

        return (true, string.Empty);
    }

    private static string ResolveSourceRoot(WindowsBackupSession currentSession)
    {
        var snapshot = NativeStorageReader.Read();
        var disk = snapshot.Disks.FirstOrDefault(d => d.Number == currentSession.SourceDiskNumber);
        var part = disk?.Partitions.FirstOrDefault(p => !p.Unallocated && p.Number == currentSession.SourcePartitionNumber);
        if (part is not null && !string.IsNullOrWhiteSpace(part.Letter))
        {
            return $@"{char.ToUpperInvariant(part.Letter.TrimEnd(':')[0])}:\";
        }

        return currentSession.SourceRootHint;
    }

    private void UpdateProgress(int percent, string message)
    {
        BackupProgressBar.Value = Math.Clamp(percent, 0, 100);
        PercentText.Text = $"{Math.Clamp(percent, 0, 100)}%";
        StageText.Text = message;
        AppendLog(message);
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        LogConsoleBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogConsoleBox.ScrollToEnd();
    }

    private void FontDecrease_Click(object sender, RoutedEventArgs e)
    {
        logFontSize = Math.Max(9.5, logFontSize - 1.5);
        LogConsoleBox.FontSize = logFontSize;
    }

    private void FontIncrease_Click(object sender, RoutedEventArgs e)
    {
        logFontSize = Math.Min(22.0, logFontSize + 1.5);
        LogConsoleBox.FontSize = logFontSize;
    }

    private void OpenCmd_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true });

    private void Restart_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("wpeutil.exe", "reboot") { UseShellExecute = false, CreateNoWindow = true });

    private void Shutdown_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("wpeutil.exe", "shutdown") { UseShellExecute = false, CreateNoWindow = true });
}
