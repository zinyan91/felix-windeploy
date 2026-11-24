using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;
using Microsoft.Win32;

namespace Felix.WindowSetup.Services;

public interface IWindowsBackupService
{
    Task<IReadOnlyList<BackupSourceInstallation>> DiscoverSourcesAsync(CancellationToken cancellationToken = default);
    bool IsSystemRebootPending();
    bool IsSystemInAuditMode();
    Task<string> CaptureDirectAsync(
        WindowsBackupRequest request,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default);
    Task<WindowsBackupSession> PrepareWinPeOrSysprepBackupAsync(
        WindowsBackupRequest request,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class NativeWindowsBackupService : IWindowsBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<BackupSourceInstallation>> DiscoverSourcesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
        var currentWinRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?
            .TrimEnd('\\') ?? string.Empty;
        var isWinPe = PeEnvironment.IsWinPe;

        var results = new List<BackupSourceInstallation>();
        foreach (var disk in snapshot.Disks)
        {
            foreach (var part in disk.Partitions)
            {
                if (part.Unallocated || string.IsNullOrWhiteSpace(part.Letter))
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(part.Letter.TrimEnd(':')[0]);
                if (letter == 'X')
                {
                    continue;
                }

                var root = $@"{letter}:\";
                var kernelPath = Path.Combine(root, "Windows", "System32", "ntoskrnl.exe");
                if (!File.Exists(kernelPath))
                {
                    continue;
                }

                var isCurrent = !isWinPe && root.TrimEnd('\\').Equals(currentWinRoot, StringComparison.OrdinalIgnoreCase);
                var (edition, version) = isCurrent
                    ? ReadLiveVersionInfo()
                    : await ReadOfflineVersionInfoAsync(root, cancellationToken);

                var hasBitLocker = part.Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase);
                results.Add(new BackupSourceInstallation(
                    root,
                    edition,
                    version,
                    isCurrent,
                    disk.Number,
                    part.Number,
                    hasBitLocker));
            }
        }

        return [.. results.OrderByDescending(s => s.IsCurrentRunningWindows).ThenBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase)];
    }

    public bool IsSystemRebootPending()
    {
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        if (cbs is not null)
        {
            return true;
        }

        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        if (wu is not null)
        {
            return true;
        }

        using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        if (sm?.GetValue("PendingFileRenameOperations") is string[] arr && arr.Length > 0)
        {
            return true;
        }

        return false;
    }

    public bool IsSystemInAuditMode()
    {
        using var state = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State");
        var imageState = state?.GetValue("ImageState") as string ?? string.Empty;
        return imageState.Contains("AUDIT", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> CaptureDirectAsync(
        WindowsBackupRequest request,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var enginePath = WindowSetupEngineLocator.Find();
        var normalizedOut = WindowsBackupMetadata.NormalizeOutputPath(request.OutputPath, request.Format);
        var outDir = Path.GetDirectoryName(normalizedOut)!;
        Directory.CreateDirectory(outDir);

        var tempConfig = Path.Combine(Path.GetTempPath(), $"felix-capture-exclude-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(tempConfig, WindowsBackupMetadata.CreateCaptureExclusionConfig(), cancellationToken);

        var markedName = WindowsBackupMetadata.EnsureImageNameMarker(request.ImageName, request.Method);
        var useSnapshot = request.Source.IsCurrentRunningWindows && !PeEnvironment.IsWinPe;

        var needsTempWimForSplitOrPatch = request.Format == BackupArchiveFormat.Swm
            || (request.MakePortableAcrossHardware && request.Method != WindowsBackupMethod.SysprepGeneralizeCapture);
        var captureTarget = needsTempWimForSplitOrPatch && request.Format == BackupArchiveFormat.Swm
            ? Path.Combine(outDir, $".felix-tmp-{Guid.NewGuid():N}.wim")
            : normalizedOut;

        try
        {
            if (File.Exists(captureTarget))
            {
                File.Delete(captureTarget);
            }

            progress?.Report((10, $"Capturing {request.Source.RootPath} with wimlib-imagex..."));
            var captureCmd = WimlibCommandBuilder.CreateCapture(
                enginePath,
                request.Source.RootPath,
                captureTarget,
                markedName,
                request.ImageDescription,
                request.Format == BackupArchiveFormat.Swm ? BackupArchiveFormat.Wim : request.Format,
                snapshot: useSnapshot,
                configFilePath: tempConfig);

            var captureRes = await WimlibProcessRunner.RunAsync(
                captureCmd,
                p =>
                {
                    if (!string.IsNullOrWhiteSpace(p.RawLine))
                    {
                        progress?.Report((p.OverallPercent, p.RawLine));
                    }
                },
                cancellationToken);
            if (captureRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"wimlib capture failed (exit {captureRes.ExitCode}): {captureRes.Output}");
            }

            if (request.MakePortableAcrossHardware && request.Method != WindowsBackupMethod.SysprepGeneralizeCapture)
            {
                progress?.Report((86, "Applying hardware portability fixes to captured SYSTEM hive..."));
                var portLogs = await BootPortabilityHelper.PatchCapturedWimPortabilityAsync(enginePath, captureTarget, cancellationToken);
                foreach (var logLine in portLogs)
                {
                    progress?.Report((88, logLine));
                }
            }

            if (request.Format == BackupArchiveFormat.Swm)
            {
                progress?.Report((90, "Splitting captured image into SWM parts..."));
                var splitCmd = WimlibCommandBuilder.CreateSplit(enginePath, captureTarget, normalizedOut, request.SplitPartSizeMiB);
                var splitRes = await WimlibProcessRunner.RunAsync(splitCmd, _ => { }, cancellationToken);
                if (splitRes.ExitCode != 0)
                {
                    throw new InvalidOperationException($"wimlib split failed (exit {splitRes.ExitCode}): {splitRes.Output}");
                }
            }

            progress?.Report((100, $"Backup saved to {normalizedOut}"));
            return normalizedOut;
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

    public async Task<WindowsBackupSession> PrepareWinPeOrSysprepBackupAsync(
        WindowsBackupRequest request,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOut = WindowsBackupMetadata.NormalizeOutputPath(request.OutputPath, request.Format);
        var peSource = FelixPeLocator.TryResolve(null, request.Source.RootPath)
            ?? throw new FileNotFoundException("Could not find FelixPE or a local WinRE image to reboot into.");

        var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
        var sourceDisk = snapshot.Disks.FirstOrDefault(d => d.Number == request.Source.DiskNumber);
        var sourcePart = sourceDisk?.Partitions.FirstOrDefault(p => p.Number == request.Source.PartitionNumber);
        var candidates = BuildBackupStagingCandidates(snapshot, request.Source.RootPath);

        var (peMount, bootWimSrc, bootSdiSrc) = await NativeDesktopHandoffService.ResolvePeFilesAsync(peSource, cancellationToken);
        string? createdOsId = null;
        string? createdDeviceId = null;
        string? createdSessionDir = null;
        (int DiskNumber, int WorkPartNumber, int WindowsPartNumber)? createdFelixWorkInfo = null;
        long createdFelixWorkOffset = 0;
        string? safetyBackupPath = null;

        try
        {
            var requiredBytes = new FileInfo(bootWimSrc).Length + new FileInfo(bootSdiSrc).Length + 512L * 1024 * 1024;
            requiredBytes = Math.Max(requiredBytes, 1_500_000_000L);

            var targetDiskFacts = sourceDisk is not null ? NativeTargetSource.Map(sourceDisk) : null;
            var targetPartFacts = targetDiskFacts?.Partitions.FirstOrDefault(p => p.Number == request.Source.PartitionNumber);
            var decision = DesktopHandoffPlanner.PlanStagingOrShrink(
                candidates,
                targetDiskFacts,
                targetPartFacts,
                InstallTargetMode.ExistingPartition,
                requiredBytes);
            if (!decision.Succeeded)
            {
                throw new InvalidOperationException(decision.Error ?? "No eligible staging volume found for WinPE backup.");
            }

            char stagingLetter;
            if (decision.RequiresFelixWorkShrink)
            {
                progress?.Report((10, "Creating temporary FELIXWORK staging partition..."));
                var workLetter = NativeDesktopHandoffService.AllocateFreeDriveLetter(snapshot);
                var shrinkScript = InstallDiskLayoutBuilder.CreateFelixWorkShrinkScript(
                    decision.ShrinkDiskNumber!.Value,
                    decision.ShrinkPartitionNumber!.Value,
                    decision.ShrinkSizeMiB,
                    workLetter);
                await DiskPart.RunAsync(shrinkScript, TimeSpan.FromMinutes(3));
                await NativeDesktopHandoffService.WaitForVolumeRootAsync(workLetter, cancellationToken);
                stagingLetter = workLetter;

                var afterShrink = await Task.Run(NativeStorageReader.Read, cancellationToken);
                var workPart = afterShrink.Disks
                    .Where(d => d.Number == decision.ShrinkDiskNumber.Value)
                    .SelectMany(d => d.Partitions)
                    .FirstOrDefault(p => !p.Unallocated && p.Letter.TrimEnd(':').Equals(workLetter.ToString(), StringComparison.OrdinalIgnoreCase));
                if (workPart is not null)
                {
                    createdFelixWorkInfo = (decision.ShrinkDiskNumber.Value, workPart.Number, decision.ShrinkPartitionNumber.Value);
                    createdFelixWorkOffset = workPart.Offset;
                }
            }
            else
            {
                stagingLetter = decision.SelectedLetter!.Value;
            }

            var sessionDir = $@"{stagingLetter}:\FelixWinDeploy";
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            Directory.CreateDirectory(sessionDir);
            createdSessionDir = sessionDir;

            var sessionId = Guid.NewGuid().ToString("N");
            var enginePath = WindowSetupEngineLocator.Find();

            var peDir = Path.Combine(sessionDir, "pe");
            Directory.CreateDirectory(peDir);
            var stagedBootWim = Path.Combine(peDir, "boot.wim");
            var stagedBootSdi = Path.Combine(peDir, "boot.sdi");

            progress?.Report((25, "Copying WinPE boot files..."));
            File.Copy(bootWimSrc, stagedBootWim, overwrite: true);
            File.Copy(bootSdiSrc, stagedBootSdi, overwrite: true);
            NativeDesktopHandoffService.ClearReadOnlyAttribute(stagedBootWim);
            NativeDesktopHandoffService.ClearReadOnlyAttribute(stagedBootSdi);

            var patchedBootCmd = Path.Combine(AppContext.BaseDirectory, "Tools", "felixpe", "felixpe-boot.cmd");
            if (!File.Exists(patchedBootCmd))
            {
                throw new FileNotFoundException("Bundled felixpe-boot.cmd was not found.", patchedBootCmd);
            }

            string? tempWinpeshlPath = null;
            if (peSource.NeedsWinpeshlInit)
            {
                tempWinpeshlPath = Path.Combine(peDir, "winpeshl.ini");
                await File.WriteAllTextAsync(
                    tempWinpeshlPath,
                    "[LaunchApps]\r\n%SystemRoot%\\System32\\cmd.exe, /c %SystemRoot%\\System32\\felixpe-boot.cmd\r\n",
                    CancellationToken.None);
            }

            try
            {
                var patchCommand = DesktopHandoffPlanner.BuildWimlibBootPatchCommand(enginePath, stagedBootWim, patchedBootCmd, tempWinpeshlPath);
                var patchResult = await WimlibProcessRunner.RunAsync(patchCommand, _ => { }, cancellationToken);
                if (patchResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to patch WinPE boot.wim (wimlib exit {patchResult.ExitCode}): {patchResult.Output}");
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempWinpeshlPath) && File.Exists(tempWinpeshlPath))
                {
                    File.Delete(tempWinpeshlPath);
                }
            }

            progress?.Report((45, "Staging Felix WinDeploy application for WinPE resume..."));
            var appDestDir = Path.Combine(sessionDir, "app");
            NativeDesktopHandoffService.CopyDirectoryFiltered(AppContext.BaseDirectory, appDestDir);

            var createdSafety = false;
            if (request.Method == WindowsBackupMethod.SysprepGeneralizeCapture)
            {
                var sysprepOpts = request.Sysprep ?? new SysprepBackupOptions();
                if (sysprepOpts.AutoRestoreSafetyBackupOnSuccess || sysprepOpts.AutoRestoreSafetyBackupOnFailure)
                {
                    progress?.Report((55, "Capturing pre-Sysprep safety backup via VSS snapshot..."));
                    var outFolder = Path.GetDirectoryName(normalizedOut)!;
                    Directory.CreateDirectory(outFolder);
                    safetyBackupPath = Path.Combine(outFolder, $"FelixSafety_{sessionId[..8]}.wim");

                    var safetyReq = new WindowsBackupRequest(
                        request.Source,
                        WindowsBackupMethod.LiveVssOrOfflineImage,
                        BackupArchiveFormat.Wim,
                        safetyBackupPath,
                        "Pre-Sysprep Safety Backup",
                        "Automatic safety snapshot captured before Sysprep",
                        MakePortableAcrossHardware: false,
                        ShutdownAfterCapture: false);
                    await CaptureDirectAsync(safetyReq, new Progress<(int Percent, string Message)>(p =>
                    {
                        progress?.Report((55 + (int)(p.Percent * 0.25), p.Message));
                    }), cancellationToken);
                    createdSafety = true;
                }

                if (sysprepOpts.StripThirdPartyDrivers)
                {
                    progress?.Report((82, "Stripping third-party drivers before Sysprep..."));
                    await StripThirdPartyDriversOnlineAsync(sysprepOpts.StripStorageDrivers, cancellationToken);
                }
            }

            progress?.Report((88, "Registering one-time WinPE boot entry..."));
            var osId = "{" + Guid.NewGuid() + "}";
            var deviceId = "{" + Guid.NewGuid() + "}";
            var firmware = FirmwareDetector.Current();
            var bcdedit = NativeDesktopHandoffService.RequireTool(SystemTools.BcdEdit);
            var bcdCommands = DesktopHandoffPlanner.BuildBcdRamdiskCommands(
                stagingLetter,
                @"\FelixWinDeploy\pe\boot.wim",
                @"\FelixWinDeploy\pe\boot.sdi",
                firmware,
                osId,
                deviceId,
                bootOnce: true);

            createdOsId = osId;
            createdDeviceId = deviceId;
            foreach (var args in bcdCommands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var res = await SystemTools.RunAsync(bcdedit, args);
                if (res.ExitCode != 0)
                {
                    throw new InvalidOperationException($"bcdedit {string.Join(' ', args)} failed (exit {res.ExitCode}): {res.Output.Trim()}");
                }
            }

            var session = new WindowsBackupSession
            {
                SessionId = sessionId,
                Stage = "Prepared",
                Method = request.Method,
                Format = request.Format,
                SourceRootHint = request.Source.RootPath,
                SourceDiskNumber = request.Source.DiskNumber,
                SourcePartitionNumber = request.Source.PartitionNumber,
                OutputPath = normalizedOut,
                SplitPartSizeMiB = request.SplitPartSizeMiB,
                ImageName = WindowsBackupMetadata.EnsureImageNameMarker(request.ImageName, request.Method),
                ImageDescription = request.ImageDescription,
                MakePortableAcrossHardware = request.MakePortableAcrossHardware,
                ShutdownAfterCapture = request.ShutdownAfterCapture,
                Sysprep = request.Sysprep,
                SafetyBackupWimPath = safetyBackupPath,
                CreatedSafetyBackup = createdSafety,
                CreatedFelixWork = createdFelixWorkInfo is not null,
                FelixWorkOffset = createdFelixWorkOffset,
                StagingDriveLetter = stagingLetter.ToString(),
                StagingRelativeDir = "FelixWinDeploy",
                WindowsBootEntryId = osId,
                RamdiskDeviceId = deviceId,
                WinPeWimRelativePath = @"FelixWinDeploy\pe\boot.wim",
                WinPeSdiRelativePath = @"FelixWinDeploy\pe\boot.sdi"
            };

            var sessionFile = Path.Combine(sessionDir, "backup-session.json");
            await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(session, JsonOptions), CancellationToken.None);

            if (request.Method == WindowsBackupMethod.SysprepGeneralizeCapture)
            {
                progress?.Report((94, "Launching sysprep.exe..."));
                await LaunchSysprepAndWaitForExitOrRebootAsync(request.Sysprep ?? new SysprepBackupOptions(), cancellationToken);
            }

            progress?.Report((100, "WinPE backup session prepared. Ready to reboot."));
            return session;
        }
        catch
        {
            await NativeDesktopHandoffService.RollbackAsync(createdOsId, createdDeviceId, createdSessionDir, createdFelixWorkInfo);
            if (!string.IsNullOrEmpty(safetyBackupPath) && File.Exists(safetyBackupPath))
            {
                try { File.Delete(safetyBackupPath); } catch { }
            }
            throw;
        }
        finally
        {
            peMount?.Dispose();
        }
    }

    private static async Task LaunchSysprepAndWaitForExitOrRebootAsync(
        SysprepBackupOptions opts,
        CancellationToken cancellationToken)
    {
        var sysprepExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Sysprep", "sysprep.exe");
        if (!File.Exists(sysprepExe))
        {
            throw new FileNotFoundException("sysprep.exe was not found on this Windows installation.", sysprepExe);
        }

        var args = new List<string>();
        if (opts.Generalize)
        {
            args.Add("/generalize");
        }
        args.Add(opts.EnterAuditMode ? "/audit" : "/oobe");
        args.Add("/reboot");
        if (!string.IsNullOrWhiteSpace(opts.UnattendXmlPath) && File.Exists(opts.UnattendXmlPath))
        {
            args.Add($"/unattend:{opts.UnattendXmlPath}");
        }

        var res = await SystemTools.RunAsync(sysprepExe, args);
        if (res.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Sysprep failed with exit code {res.ExitCode}. Inspect %WINDIR%\\System32\\Sysprep\\Panther\\setuperr.log. {res.Output.Trim()}");
        }
    }

    private static async Task StripThirdPartyDriversOnlineAsync(bool includeStorageDrivers, CancellationToken cancellationToken)
    {
        var pnputil = SystemTools.Find("pnputil.exe");
        if (string.IsNullOrEmpty(pnputil))
        {
            return;
        }

        var enumRes = await SystemTools.RunAsync(pnputil, ["/enum-drivers"]);
        if (enumRes.ExitCode != 0)
        {
            return;
        }

        var blocks = enumRes.Output.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? publishedName = null;
            string? className = null;
            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.Trim();
                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                if (key.Contains("Published Name", StringComparison.OrdinalIgnoreCase))
                {
                    publishedName = val;
                }
                else if (key.Contains("Class Name", StringComparison.OrdinalIgnoreCase))
                {
                    className = val;
                }
            }

            if (string.IsNullOrWhiteSpace(publishedName) || !publishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!includeStorageDrivers
                && !string.IsNullOrWhiteSpace(className)
                && (className.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)
                    || className.Equals("HDC", StringComparison.OrdinalIgnoreCase)
                    || className.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await SystemTools.RunAsync(pnputil, ["/delete-driver", publishedName, "/uninstall", "/force"]);
        }
    }

    private static List<StagingVolumeCandidate> BuildBackupStagingCandidates(StorageSnapshot snapshot, string sourceRootPath)
    {
        var sourceLetter = sourceRootPath.Length >= 2 && sourceRootPath[1] == ':'
            ? char.ToUpperInvariant(sourceRootPath[0])
            : (char?)null;
        var list = new List<StagingVolumeCandidate>();

        foreach (var disk in snapshot.Disks)
        {
            var isRemovable = disk.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase);
            foreach (var part in disk.Partitions)
            {
                if (part.Unallocated || string.IsNullOrWhiteSpace(part.Letter))
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(part.Letter.TrimEnd(':')[0]);
                if (letter is < 'C' or > 'Z')
                {
                    continue;
                }

                list.Add(new StagingVolumeCandidate(
                    letter,
                    part.Free,
                    part.FileSystem,
                    part.Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase),
                    isRemovable,
                    IsOnTargetDisk: false,
                    IsTargetWindowsPartition: false,
                    HoldsSourceImage: sourceLetter == letter));
            }
        }

        return list;
    }

    private static (string Edition, string Version) ReadLiveVersionInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
        var build = key?.GetValue("CurrentBuild") as string ?? "";
        var version = string.IsNullOrEmpty(display) ? build : string.IsNullOrEmpty(build) ? display : $"{display} / {build}";
        return (product, version);
    }

    private static async Task<(string Edition, string Version)> ReadOfflineVersionInfoAsync(string windowsDriveRoot, CancellationToken cancellationToken)
    {
        var softwareHive = Path.Combine(windowsDriveRoot, "Windows", "System32", "config", "SOFTWARE");
        var regExe = SystemTools.Find("reg.exe");
        if (!File.Exists(softwareHive) || string.IsNullOrEmpty(regExe))
        {
            return ("Windows (Offline)", "");
        }

        var subKey = $"FelixBakSoft_{Guid.NewGuid():N}";
        var load = await SystemTools.RunAsync(regExe, ["load", $@"HKLM\{subKey}", softwareHive]);
        if (load.ExitCode != 0)
        {
            return ("Windows (Offline)", "");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var key = Registry.LocalMachine.OpenSubKey($@"{subKey}\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows (Offline)";
            var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            var build = key?.GetValue("CurrentBuild") as string ?? "";
            var version = string.IsNullOrEmpty(display) ? build : string.IsNullOrEmpty(build) ? display : $"{display} / {build}";
            return (product, version);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await SystemTools.RunAsync(regExe, ["unload", $@"HKLM\{subKey}"]);
        }
    }
}
