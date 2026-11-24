using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed record StagedFirstBootArtifacts(
    string? StagingRoot,
    IReadOnlyDictionary<RemoteSupportAppKind, string> StagedRemoteInstallers,
    string? StagedFirstDesktopSourcePath);

public interface IFirstBootStagingService
{
    Task<(StagedFirstBootArtifacts Artifacts, IReadOnlyList<string> Logs)> StagePreFormatAsync(
        FirstBootOptions options,
        string stagingRoot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ApplyOfflineAsync(
        PreparedInstallation installation,
        string windowsRoot,
        StagedDriverArtifacts? stagedDrivers,
        StagedFirstBootArtifacts? stagedFirstBoot,
        CancellationToken cancellationToken = default);
}

public sealed class NativeFirstBootStagingService : IFirstBootStagingService
{
    public async Task<(StagedFirstBootArtifacts Artifacts, IReadOnlyList<string> Logs)> StagePreFormatAsync(
        FirstBootOptions options,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        var stagedRemote = new Dictionary<RemoteSupportAppKind, string>();
        Directory.CreateDirectory(stagingRoot);

        var enabledApps = (options.RemoteApps ?? []).Where(a => a.Enabled).ToList();
        if (enabledApps.Count > 0)
        {
            var remoteDir = Path.Combine(stagingRoot, "Remote");
            Directory.CreateDirectory(remoteDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            foreach (var app in enabledApps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = RemoteSupportCatalog.DownloadUrl(app.Kind);
                var dest = Path.Combine(remoteDir, RemoteSupportCatalog.InstallerFileName(app.Kind));
                try
                {
                    using var response = await http.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(dest);
                    await response.Content.CopyToAsync(fs, cancellationToken);
                    stagedRemote[app.Kind] = dest;
                    logs.Add($"Pre-downloaded {RemoteSupportCatalog.DisplayName(app.Kind)} -> {dest}");
                }
                catch (Exception ex)
                {
                    logs.Add($"Warning: pre-download failed for {RemoteSupportCatalog.DisplayName(app.Kind)} ({ex.Message}); First Boot will retry online.");
                }
            }
        }

        string? stagedDesktopSource = null;
        if (options.HasFirstDesktopApp && File.Exists(options.FirstDesktopApp!.SourcePath))
        {
            var desktopDir = Path.Combine(stagingRoot, "FirstDesktop");
            Directory.CreateDirectory(desktopDir);
            var src = options.FirstDesktopApp.SourcePath!;
            stagedDesktopSource = Path.Combine(desktopDir, Path.GetFileName(src));
            File.Copy(src, stagedDesktopSource, overwrite: true);
            logs.Add($"Staged at-first-desktop source {src} -> {stagedDesktopSource}");
        }

        return (new StagedFirstBootArtifacts(stagingRoot, stagedRemote, stagedDesktopSource), logs);
    }

    public async Task<IReadOnlyList<string>> ApplyOfflineAsync(
        PreparedInstallation installation,
        string windowsRoot,
        StagedDriverArtifacts? stagedDrivers,
        StagedFirstBootArtifacts? stagedFirstBoot,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        var firstBootOptions = installation.Request.Options?.FirstBoot;
        var firstBootRoot = Path.Combine(windowsRoot, "ProgramData", "FelixWinDeploy", "FirstBoot");
        Directory.CreateDirectory(firstBootRoot);

        var desktopLaunchCommands = new List<string>();

        if (stagedFirstBoot is not null)
        {
            var remoteDestDir = Path.Combine(firstBootRoot, "Remote");
            foreach (var (kind, srcPath) in stagedFirstBoot.StagedRemoteInstallers)
            {
                if (!File.Exists(srcPath))
                {
                    continue;
                }

                Directory.CreateDirectory(remoteDestDir);
                var destPath = Path.Combine(remoteDestDir, Path.GetFileName(srcPath));
                File.Copy(srcPath, destPath, overwrite: true);
                logs.Add($"Copied {RemoteSupportCatalog.DisplayName(kind)} installer to {destPath}");

                if (kind == RemoteSupportAppKind.TeamViewerPortable)
                {
                    var tvDir = Path.Combine(windowsRoot, "Felix", "Remote", "TeamViewer");
                    Directory.CreateDirectory(tvDir);
                    ZipFile.ExtractToDirectory(destPath, tvDir, overwriteFiles: true);
                    await File.WriteAllTextAsync(
                        Path.Combine(tvDir, "TeamViewer.ini"),
                        "[TeamViewer]\r\nTermsOfUseAccepted=1\r\n",
                        cancellationToken);
                    desktopLaunchCommands.Add(@"C:\Felix\Remote\TeamViewer\TeamViewer.exe");
                    logs.Add($"Extracted TeamViewer Portable to {tvDir}");
                }
            }
        }

        if (firstBootOptions?.HasFirstDesktopApp == true)
        {
            var appOptions = firstBootOptions.FirstDesktopApp!;
            var sourcePath = stagedFirstBoot?.StagedFirstDesktopSourcePath ?? appOptions.SourcePath;
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                var toolName = SanitizeFolderName(Path.GetFileNameWithoutExtension(sourcePath));
                var toolsOfflineDir = Path.Combine(windowsRoot, "Felix", "Tools", toolName);
                Directory.CreateDirectory(toolsOfflineDir);

                if (appOptions.Mode == FirstDesktopAppMode.PortableExe)
                {
                    var exeName = Path.GetFileName(sourcePath);
                    File.Copy(sourcePath, Path.Combine(toolsOfflineDir, exeName), overwrite: true);
                    desktopLaunchCommands.Add($@"C:\Felix\Tools\{toolName}\{exeName}");
                    logs.Add($"Staged portable EXE to {toolsOfflineDir}");
                }
                else if (appOptions.Mode == FirstDesktopAppMode.ZipArchive
                    && !string.IsNullOrWhiteSpace(appOptions.ProgramInsideZip))
                {
                    ZipFile.ExtractToDirectory(sourcePath, toolsOfflineDir, overwriteFiles: true);
                    var relExe = appOptions.ProgramInsideZip.Replace('/', '\\').TrimStart('\\');
                    desktopLaunchCommands.Add($@"C:\Felix\Tools\{toolName}\{relExe}");
                    logs.Add($"Extracted first-desktop ZIP to {toolsOfflineDir}");
                }
            }
        }

        string? desktopLauncherInsideWindows = null;
        if (desktopLaunchCommands.Count > 0)
        {
            var launcherOfflinePath = Path.Combine(firstBootRoot, "firstboot-desktop.cmd");
            await File.WriteAllTextAsync(
                launcherOfflinePath,
                FirstBootScriptBuilder.BuildDesktopLauncherCmd(desktopLaunchCommands),
                cancellationToken);
            desktopLauncherInsideWindows = @"C:\ProgramData\FelixWinDeploy\FirstBoot\firstboot-desktop.cmd";
            logs.Add($"Wrote First Boot desktop launcher to {launcherOfflinePath}");
        }

        var hasDriversRetry = Directory.Exists(Path.Combine(firstBootRoot, "Drivers"));
        var hasManualRetry = Directory.Exists(Path.Combine(firstBootRoot, "ManualDrivers"));
        var hasWifi = Directory.Exists(Path.Combine(firstBootRoot, "Wifi"));
        var hasPrinters = File.Exists(Path.Combine(firstBootRoot, "Printers", "Printers.printerExport"));
        var enabledApps = (firstBootOptions?.RemoteApps ?? []).Where(a => a.Enabled).ToList();
        var enableWinRe = (installation.Request.Options ?? new()).CreateRecovery;
        var setBootTimeout = installation.Request.Options?.BootAndRecovery?.AddParallelWindows == true
            || !string.IsNullOrWhiteSpace(installation.Request.Options?.BootAndRecovery?.ExtraWinPePath);
        var writeReport = firstBootOptions?.WriteDesktopSummaryReport ?? false;

        if (hasDriversRetry || hasManualRetry || hasWifi || hasPrinters
            || enabledApps.Count > 0 || desktopLauncherInsideWindows is not null)
        {
            var scriptsDir = Path.Combine(windowsRoot, "Windows", "Setup", "Scripts");
            Directory.CreateDirectory(scriptsDir);
            var setupCompletePath = Path.Combine(scriptsDir, "SetupComplete.cmd");
            var spec = new FirstBootScriptSpec(
                HasDriversRetry: hasDriversRetry,
                HasManualDriversRetry: hasManualRetry,
                HasWifiProfiles: hasWifi,
                HasPrinterBackup: hasPrinters,
                EnableWinRe: enableWinRe,
                SetBootTimeoutTenSeconds: setBootTimeout,
                EnabledRemoteApps: enabledApps,
                DesktopLauncherPath: desktopLauncherInsideWindows,
                WriteDesktopSummaryReport: writeReport);
            await File.WriteAllTextAsync(
                setupCompletePath,
                FirstBootScriptBuilder.BuildSetupCompleteCmd(spec),
                cancellationToken);
            logs.Add($"Wrote SetupComplete script to {setupCompletePath}");
        }

        if (writeReport)
        {
            var publicDesktopDir = Path.Combine(windowsRoot, "Users", "Public", "Desktop");
            Directory.CreateDirectory(publicDesktopDir);
            var reportPath = Path.Combine(publicDesktopDir, "FelixWinDeploy.txt");
            await File.WriteAllTextAsync(
                reportPath,
                InstallSummaryReportBuilder.Build(installation.Request, stagedDrivers),
                cancellationToken);
            logs.Add($"Wrote desktop summary report to {reportPath}");
        }

        var manifestPath = Path.Combine(firstBootRoot, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                GeneratedUtc = DateTime.UtcNow,
                Edition = installation.Request.Source.Edition,
                HasDriversRetry = hasDriversRetry,
                HasWifi = hasWifi,
                HasPrinters = hasPrinters,
                RemoteApps = enabledApps.Select(a => a.Kind.ToString()).ToArray(),
                DesktopLauncher = desktopLauncherInsideWindows
            }, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return logs;
    }

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "App";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }
}
