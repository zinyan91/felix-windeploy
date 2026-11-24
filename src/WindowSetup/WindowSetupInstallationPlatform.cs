using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup;

public sealed class WindowSetupInstallationPlatform : IInstallationPlatform
{
    private readonly InstallStorageCoordinator storage = new();
    private readonly string enginePath;
    private readonly IDriverStoreService driverStore;
    private readonly INetworkAndPrinterBackupService networkBackup;
    private readonly IExtraBootEntriesService extraBootEntries;
    private readonly IFirstBootStagingService firstBootStaging;
    private readonly IBloatwareService bloatwareService;
    private readonly StagedDriverArtifacts? preStagedDrivers;
    private StagedFirstBootArtifacts? stagedFirstBoot;

    public WindowSetupInstallationPlatform(
        string enginePath,
        IDriverStoreService? driverStore = null,
        INetworkAndPrinterBackupService? networkBackup = null,
        IExtraBootEntriesService? extraBootEntries = null,
        IFirstBootStagingService? firstBootStaging = null,
        StagedDriverArtifacts? preStagedDrivers = null,
        StagedFirstBootArtifacts? preStagedFirstBoot = null,
        IBloatwareService? bloatwareService = null)
    {
        this.enginePath = Path.GetFullPath(enginePath);
        this.driverStore = driverStore ?? new NativeDriverStoreService();
        this.networkBackup = networkBackup ?? new NativeNetworkAndPrinterBackupService();
        this.extraBootEntries = extraBootEntries ?? new NativeExtraBootEntriesService();
        this.firstBootStaging = firstBootStaging ?? new NativeFirstBootStagingService();
        this.bloatwareService = bloatwareService ?? new NativeBloatwareService();
        this.preStagedDrivers = preStagedDrivers;
        stagedFirstBoot = preStagedFirstBoot;
    }

    internal StagedFirstBootArtifacts? LastStagedFirstBoot => stagedFirstBoot;

    public async Task<StagedDriverArtifacts?> StageDriversAndNetworkAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (preStagedDrivers is not null || stagedFirstBoot is not null)
        {
            if (request.Options?.Drivers?.WinPeDriversFolder is { } winPeDir && Directory.Exists(winPeDir))
            {
                await driverStore.LoadWinPeDriversAsync(winPeDir, cancellationToken);
            }

            return preStagedDrivers;
        }

        var options = request.Options?.Drivers;
        var firstBootOptions = request.Options?.FirstBoot;
        stagedFirstBoot = null;
        if (options?.HasBackupOrStagingWork != true && firstBootOptions?.HasPreFormatStagingWork != true)
        {
            return null;
        }

        var log = new List<string>();
        var stagingRoot = Path.Combine(runDirectory, "staged");
        Directory.CreateDirectory(stagingRoot);

        if (options is not null && !string.IsNullOrWhiteSpace(options.WinPeDriversFolder) && Directory.Exists(options.WinPeDriversFolder))
        {
            var peResult = await driverStore.LoadWinPeDriversAsync(options.WinPeDriversFolder, cancellationToken);
            log.Add($"WinPE drivers ({options.WinPeDriversFolder}): Succeeded={peResult.Succeeded}");
            log.AddRange(peResult.Errors);
        }

        string? driversDir = null;
        var stagedCount = 0;
        var wantsBackup = options is not null && (options.BackupPreset != DriverBackupPreset.None
            || (options.SelectedDriverPublishedNames is { Count: > 0 }));

        if (wantsBackup && options is not null)
        {
            var allDrivers = await driverStore.ListThirdPartyDriversAsync(cancellationToken);
            IReadOnlyList<InstalledDriverEntry> chosen;
            if (options.SelectedDriverPublishedNames is { Count: > 0 })
            {
                var selectedSet = new HashSet<string>(
                    options.SelectedDriverPublishedNames,
                    StringComparer.OrdinalIgnoreCase);
                chosen = allDrivers.Where(d => selectedSet.Contains(d.PublishedName)).ToList();
            }
            else
            {
                chosen = DriverSelectionPolicy.SelectByPreset(allDrivers, options.BackupPreset, keepLatestOnly: true);
            }

            if (chosen.Count > 0)
            {
                driversDir = Path.Combine(stagingRoot, "Drivers");
                var exportResult = await driverStore.ExportSelectedDriversAsync(chosen, driversDir, cancellationToken);
                stagedCount = exportResult.ExportedCount;
                log.Add($"Driver backup: requested={chosen.Count}, exported={exportResult.ExportedCount}");
                log.AddRange(exportResult.Errors);
            }
            else
            {
                log.Add("Driver backup: 0 matching third-party drivers selected.");
            }
        }

        string? wifiPath = null;
        if (options?.ExportConnectedWifi == true)
        {
            var wifiResult = await networkBackup.ExportConnectedWifiProfileAsync(
                Path.Combine(stagingRoot, "Wifi"),
                cancellationToken);
            wifiPath = wifiResult.ArtifactPath;
            log.Add($"Wi-Fi export: {wifiResult.Message}");
        }

        string? printerPath = null;
        if (options?.BackupPrinters == true)
        {
            var printerResult = await networkBackup.ExportPrintersAsync(
                Path.Combine(stagingRoot, "Printers"),
                cancellationToken);
            printerPath = printerResult.ArtifactPath;
            log.Add($"Printer backup: {printerResult.Message}");
        }

        if (firstBootOptions?.HasPreFormatStagingWork == true)
        {
            var (fbArtifacts, fbLogs) = await firstBootStaging.StagePreFormatAsync(
                firstBootOptions,
                Path.Combine(stagingRoot, "FirstBoot"),
                cancellationToken);
            stagedFirstBoot = fbArtifacts;
            log.AddRange(fbLogs);
        }

        await File.WriteAllLinesAsync(
            Path.Combine(runDirectory, "stage-drivers.log"),
            log,
            CancellationToken.None);

        return new StagedDriverArtifacts(driversDir, wifiPath, printerPath, stagedCount);
    }

    public async Task<EngineExecutionResult> VerifySourceAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        IsoMountLease? mount = null;
        try
        {
            (mount, var wimPath) = await ResolveInstallImageAsync(request.Source.ImagePath, cancellationToken);
            var command = WimlibCommandBuilder.CreateVerify(enginePath, wimPath);
            await File.WriteAllLinesAsync(
                Path.Combine(runDirectory, "verify-command.txt"),
                new[] { command.ExecutablePath }.Concat(command.Arguments),
                CancellationToken.None);

            return await WimlibProcessRunner.RunAsync(
                command,
                p => progress?.Report(new(
                    InstallRunStage.VerifySource,
                    InstallStageState.Running,
                    p.RawLine,
                    8 + (int)Math.Round(p.OverallPercent * 0.10))),
                cancellationToken);
        }
        finally
        {
            mount?.Dispose();
        }
    }

    public Task<PreparedInstallation> PrepareTargetAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        storage.PrepareAsync(request, runDirectory, progress);

    public async Task<EngineExecutionResult> ApplyWindowsAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var windowsLetter = RequireLetter(installation.Request.Target.WindowsPartition);
        var windowsRoot = $@"{windowsLetter}:\";

        IsoMountLease? mount = null;
        try
        {
            (mount, var wimPath) = await ResolveInstallImageAsync(
                installation.Request.Source.ImagePath,
                CancellationToken.None);
            var command = WimlibCommandBuilder.CreateApply(
                enginePath,
                wimPath,
                installation.Request.Source.ImageIndex,
                windowsRoot);
            await File.WriteAllLinesAsync(
                Path.Combine(installation.RunDirectory, "engine-command.txt"),
                new[] { command.ExecutablePath }.Concat(command.Arguments),
                CancellationToken.None);

            var result = await WimlibProcessRunner.RunAsync(
                command,
                p => progress?.Report(new(
                    InstallRunStage.ApplyWindows,
                    InstallStageState.Running,
                    p.RawLine,
                    28 + (int)Math.Round(p.OverallPercent * 0.44))),
                CancellationToken.None);

            if (result.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(installation.Request.AutounattendPath))
                {
                    var pantherDir = Path.Combine(windowsRoot, "Windows", "Panther");
                    Directory.CreateDirectory(pantherDir);
                    File.Copy(
                        installation.Request.AutounattendPath,
                        Path.Combine(pantherDir, "unattend.xml"),
                        overwrite: true);
                }
                else if (installation.Request.Options?.GeneratedUnattend is { } generatedSpec)
                {
                    var pantherDir = Path.Combine(windowsRoot, "Windows", "Panther");
                    Directory.CreateDirectory(pantherDir);
                    var spec = generatedSpec with
                    {
                        BypassMicrosoftAccount = (installation.Request.Options ?? new()).BypassMicrosoftAccount
                    };
                    await File.WriteAllTextAsync(
                        Path.Combine(pantherDir, "unattend.xml"),
                        UnattendGenerator.Generate(spec),
                        CancellationToken.None);
                }
                else if ((installation.Request.Options ?? new()).BypassMicrosoftAccount)
                {
                    var pantherDir = Path.Combine(windowsRoot, "Windows", "Panther");
                    Directory.CreateDirectory(pantherDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(pantherDir, "unattend.xml"),
                        UnattendGenerator.GenerateBypassMicrosoftAccountOnly(),
                        CancellationToken.None);
                }
            }

            return result;
        }
        finally
        {
            mount?.Dispose();
        }
    }

    public async Task InjectDriversAndNetworkAsync(
        PreparedInstallation installation,
        StagedDriverArtifacts? staged,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var windowsLetter = RequireLetter(installation.Request.Target.WindowsPartition);
        var windowsRoot = $@"{windowsLetter}:\";
        var firstBootRoot = Path.Combine(windowsRoot, "ProgramData", "FelixWindowSetup", "FirstBoot");
        var options = installation.Request.Options?.Drivers;
        var log = new List<string>();

        if (staged?.BackedUpDriversDirectory is { } backedUpDir && Directory.Exists(backedUpDir))
        {
            var injectResult = await driverStore.InjectOfflineDriversAsync(
                windowsRoot,
                backedUpDir,
                recurse: true,
                cancellationToken);
            log.Add($"Offline inject backed-up drivers: Succeeded={injectResult.Succeeded}");
            log.AddRange(injectResult.Errors);

            var retryDriversDir = Path.Combine(firstBootRoot, "Drivers");
            CopyDirectoryRecursive(backedUpDir, retryDriversDir);
            log.Add($"Staged backed-up drivers for First Boot retry at {retryDriversDir}");
        }

        if (!string.IsNullOrWhiteSpace(options?.WindowsDriversFolder)
            && Directory.Exists(options.WindowsDriversFolder))
        {
            var manualResult = await driverStore.InjectOfflineDriversAsync(
                windowsRoot,
                options.WindowsDriversFolder,
                recurse: true,
                cancellationToken);
            log.Add($"Offline inject manual Windows drivers ({options.WindowsDriversFolder}): Succeeded={manualResult.Succeeded}");
            log.AddRange(manualResult.Errors);

            if (!manualResult.Succeeded)
            {
                var retryManualDir = Path.Combine(firstBootRoot, "ManualDrivers");
                CopyDirectoryRecursive(options.WindowsDriversFolder, retryManualDir);
                log.Add($"Staged manual drivers for First Boot retry at {retryManualDir}");
            }
        }

        if (!string.IsNullOrWhiteSpace(staged?.WifiProfilePath) && File.Exists(staged.WifiProfilePath))
        {
            var wifiDir = Path.Combine(firstBootRoot, "Wifi");
            Directory.CreateDirectory(wifiDir);
            var targetWifi = Path.Combine(wifiDir, Path.GetFileName(staged.WifiProfilePath));
            File.Copy(staged.WifiProfilePath, targetWifi, overwrite: true);
            log.Add($"Staged Wi-Fi profile to {targetWifi}");
        }

        if (!string.IsNullOrWhiteSpace(staged?.PrinterBackupPath) && File.Exists(staged.PrinterBackupPath))
        {
            var printerDir = Path.Combine(firstBootRoot, "Printers");
            Directory.CreateDirectory(printerDir);
            var targetPrinter = Path.Combine(printerDir, Path.GetFileName(staged.PrinterBackupPath));
            File.Copy(staged.PrinterBackupPath, targetPrinter, overwrite: true);
            log.Add($"Staged printer backup to {targetPrinter}");
        }

        if (installation.Request.Options?.FirstBoot?.HasOfflineWork == true || stagedFirstBoot is not null)
        {
            var fbLogs = await firstBootStaging.ApplyOfflineAsync(
                installation,
                windowsRoot,
                staged,
                stagedFirstBoot,
                cancellationToken);
            log.AddRange(fbLogs);
        }

        if (installation.Request.Options?.Bloatware is { HasWork: true } bloatware)
        {
            var bloatLogs = await bloatwareService.ApplyOfflineRemovalAsync(windowsRoot, bloatware, cancellationToken);
            log.AddRange(bloatLogs);
        }

        if (WindowsBackupMetadata.IsInPlaceBackupEdition(installation.Request.Source.Edition))
        {
            var portLogs = await BootPortabilityHelper.ApplyToOfflineWindowsAsync(windowsRoot, cancellationToken);
            log.AddRange(portLogs);
        }

        await File.WriteAllLinesAsync(
            Path.Combine(installation.RunDirectory, "inject-drivers.log"),
            log,
            CancellationToken.None);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(
                subDir,
                Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    public async Task ConfigureBootAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var windows = RequireLetter(installation.Request.Target.WindowsPartition);
        var system = RequireLetter(installation.Request.Target.EfiSystemPartition);
        var isUefi = installation.Request.Target.Firmware == FirmwareMode.UefiGpt;
        var firmware = isUefi ? "UEFI" : "BIOS";
        var clearBootMenu = (installation.Request.Options ?? new()).ClearBootMenu;
        var preferBootEx = false;
        if (isUefi)
        {
            var host = HardwareCapabilityDetector.DetectSecureBoot(FirmwareMode.UefiGpt);
            var hasBootEx = File.Exists($@"{windows}:\Windows\Boot\EFI_EX\bootmgfw_EX.efi");
            var assessment = SecureBootCheck.Evaluate(host, hasBootEx);
            preferBootEx = assessment.PreferBootEx;
            await File.WriteAllTextAsync(
                Path.Combine(installation.RunDirectory, "secureboot.txt"),
                $"{assessment.Status}: {assessment.Code} - {assessment.Message}",
                CancellationToken.None);
        }

        await WindowsBootRepair.ApplyAsync(
            new BootToolTarget(),
            new WindowsBootRepairPlan(windows, system, firmware, clearBootMenu, preferBootEx),
            cancellationToken);

        var bootOptions = installation.Request.Options?.BootAndRecovery;
        if (bootOptions is not null && (bootOptions.AddParallelWindows || !string.IsNullOrWhiteSpace(bootOptions.ExtraWinPePath)))
        {
            var extraLogs = new List<string>();
            if (bootOptions.AddParallelWindows)
            {
                extraLogs.AddRange(await extraBootEntries.AddParallelWindowsEntriesAsync(
                    system,
                    windows,
                    installation.Request.Target.Firmware,
                    cancellationToken));
            }

            if (!string.IsNullOrWhiteSpace(bootOptions.ExtraWinPePath))
            {
                extraLogs.AddRange(await extraBootEntries.AddExtraWinPeEntryAsync(
                    system,
                    windows,
                    installation.Request.Target.Firmware,
                    bootOptions.ExtraWinPePath,
                    bootOptions.ExtraWinPeTitle ?? "Felix WinPE",
                    cancellationToken));
            }

            await File.WriteAllLinesAsync(
                Path.Combine(installation.RunDirectory, "boot-extra.log"),
                extraLogs,
                CancellationToken.None);
        }
    }

    public async Task ConfigureRecoveryAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var windows = RequireLetter(installation.Request.Target.WindowsPartition);
        var source = $@"{windows}:\Windows\System32\Recovery\Winre.wim";
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Windows Recovery image was not found after apply.", source);
        }

        char recovery;
        if (installation.Request.Target.RecoveryPartition.Identity != "none")
        {
            recovery = RequireLetter(installation.Request.Target.RecoveryPartition);
        }
        else if (installation.Request.Options?.BootAndRecovery?.CreateNewRecovery == true)
        {
            var requiredMiB = RecoverySizeCalculator.CalculateRequiredMiB(new FileInfo(source).Length);
            recovery = await storage.ShrinkWindowsForRecoveryAsync(installation, requiredMiB);
        }
        else
        {
            return;
        }

        var destination = $@"{recovery}:\Recovery\WindowsRE";
        Directory.CreateDirectory(destination);
        var destWim = Path.Combine(destination, "Winre.wim");
        if (!File.Exists(destWim))
        {
            File.Copy(source, destWim, false);
        }

        var reagent = Path.Combine($@"{windows}:\Windows", "System32", "reagentc.exe");
        if (!File.Exists(reagent))
        {
            reagent = Path.Combine(Environment.SystemDirectory, "reagentc.exe");
        }
        if (!File.Exists(reagent))
        {
            throw new FileNotFoundException("reagentc.exe is not available in this environment.", reagent);
        }
        var result = await SystemTools.RunAsync(
            reagent,
            [
                "/setreimage",
                "/path",
                destination,
                "/target",
                $@"{windows}:\Windows"
            ]);
        await File.WriteAllTextAsync(
            Path.Combine(installation.RunDirectory, "reagentc.log"),
            result.Output,
            CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"reagentc.exe exited with code {result.ExitCode}.{Environment.NewLine}{result.Output.Trim()}");
        }
    }

    public Task<InstallationVerificationResult> VerifyAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var target = installation.Request.Target;
        var windows = RequireLetter(target.WindowsPartition);
        var system = RequireLetter(target.EfiSystemPartition);
        var uefi = target.Firmware == FirmwareMode.UefiGpt;
        var required = new[]
        {
            $@"{windows}:\Windows\System32\config\SYSTEM",
            $@"{windows}:\Windows\System32\winload.{(uefi ? "efi" : "exe")}",
            uefi
                ? $@"{system}:\EFI\Microsoft\Boot\BCD"
                : $@"{system}:\Boot\BCD",
            uefi
                ? $@"{system}:\EFI\Microsoft\Boot\bootmgfw.efi"
                : $@"{system}:\bootmgr"
        }.ToList();
        if ((installation.Request.Options ?? new()).CreateRecovery &&
            target.RecoveryPartition.Identity != "none")
        {
            var recovery = RequireLetter(target.RecoveryPartition);
            required.Add($@"{recovery}:\Recovery\WindowsRE\Winre.wim");
        }
        var missing = required.Where(path => !File.Exists(path)).ToArray();
        return Task.FromResult(new InstallationVerificationResult(
            File.Exists(required[0]),
            File.Exists(required[1]),
            File.Exists(required[2]),
            missing));
    }

    public async Task<IReadOnlyList<BcdEntry>> ReadBootEntriesAsync(
        PreparedInstallation installation,
        CancellationToken cancellationToken)
    {
        var target = installation.Request.Target;
        var system = RequireLetter(target.EfiSystemPartition);
        var store = target.Firmware == FirmwareMode.UefiGpt
            ? $@"{system}:\EFI\Microsoft\Boot\BCD"
            : $@"{system}:\Boot\BCD";
        if (!File.Exists(store))
        {
            return [];
        }

        var bcdedit = SystemTools.Find(SystemTools.BcdEdit)
            ?? throw new FileNotFoundException("bcdedit.exe is not available.", SystemTools.BcdEdit);
        var result = await SystemTools.RunAsync(bcdedit, ["/store", store, "/enum", "all", "/v"]);
        if (result.ExitCode != 0)
        {
            return [];
        }
        return BootMenu.Parse(result.Output);
    }

    public Task<string?> ExportRunLogAsync(
        PreparedInstallation installation,
        string runDirectory)
    {
        try
        {
            var accessPath = installation.Request.Target.WindowsPartition.AccessPath;
            if (accessPath.Length < 2)
            {
                return Task.FromResult<string?>(null);
            }

            var destination = Path.Combine(
                accessPath + "\\",
                "Windows",
                "Logs",
                "WindowSetup",
                Path.GetFileName(runDirectory));
            CopyDirectory(runDirectory, destination);
            return Task.FromResult<string?>(destination);
        }
        catch (IOException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task CleanupAsync(
        PreparedInstallation installation,
        bool succeeded) =>
        storage.CleanupAsync(installation, succeeded);

    private static async Task<(IsoMountLease? Mount, string InstallImagePath)> ResolveInstallImageAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(imagePath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return (null, imagePath);
        }

        var mount = await IsoMountLease.AttachAsync(imagePath, cancellationToken);
        var installImage = new[]
            {
                Path.Combine(mount.RootPath, "sources", "install.wim"),
                Path.Combine(mount.RootPath, "sources", "install.esd"),
                Path.Combine(mount.RootPath, "sources", "install.swm")
            }
            .FirstOrDefault(File.Exists);

        if (installImage is null)
        {
            mount.Dispose();
            throw new InvalidOperationException("The ISO does not contain sources\\install.wim, install.esd, or install.swm.");
        }

        return (mount, installImage);
    }

    private static char RequireLetter(PartitionTarget partition)
    {
        if (partition.AccessPath.Length < 2 ||
            partition.AccessPath[1] != ':' ||
            char.ToUpperInvariant(partition.AccessPath[0]) is < 'D' or > 'Z')
        {
            throw new InvalidOperationException($"{partition.DisplayName} has no usable access path.");
        }
        return char.ToUpperInvariant(partition.AccessPath[0]);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private sealed class BootToolTarget : IWindowsBootTarget
    {
        public async Task<(int ExitCode, string Output)> RunAsync(BootToolCommand command)
        {
            var tool = Path.IsPathRooted(command.Tool)
                ? (File.Exists(command.Tool) ? command.Tool : throw new FileNotFoundException($"{command.Tool} is not available.", command.Tool))
                : (SystemTools.Find(command.Tool) ?? throw new FileNotFoundException($"{command.Tool} is not available.", command.Tool));
            var result = await SystemTools.RunAsync(tool, command.Arguments);
            return (result.ExitCode, result.Output);
        }

        public bool HasFile(string path) => File.Exists(path);
    }
}
