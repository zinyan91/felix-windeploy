using System.IO;
using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed record PreparedDesktopHandoff(
    string SessionDirectory,
    string SessionFilePath,
    char StagingDriveLetter,
    string BcdOsId,
    string BcdDeviceId,
    DesktopHandoffSession Session);

public interface IDesktopHandoffService
{
    Task<PreparedDesktopHandoff> PrepareAsync(
        WindowSetupRequest request,
        FelixPeSource peSource,
        IDriverStoreService driverStore,
        INetworkAndPrinterBackupService networkBackup,
        IFirstBootStagingService firstBootStaging,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task CancelPreparedHandoffAsync(
        PreparedDesktopHandoff handoff,
        CancellationToken cancellationToken = default);

    Task CompleteWinPeResumeCleanupAsync(
        DesktopHandoffSession session,
        string sessionFilePath,
        char systemLetter,
        FirmwareMode firmware,
        CancellationToken cancellationToken = default);

    Task RollbackToOldWindowsFromWinPeAsync(
        DesktopHandoffSession session,
        string sessionFilePath,
        CancellationToken cancellationToken = default);
}

public sealed class NativeDesktopHandoffService : IDesktopHandoffService
{
    public async Task<PreparedDesktopHandoff> PrepareAsync(
        WindowSetupRequest request,
        FelixPeSource peSource,
        IDriverStoreService driverStore,
        INetworkAndPrinterBackupService networkBackup,
        IFirstBootStagingService firstBootStaging,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(peSource);

        var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
        StorageSnapshotRules.Check(snapshot);

        IsoMountLease? peMount = null;
        string? createdSessionDir = null;
        string? createdOsId = null;
        string? createdDeviceId = null;
        (int DiskNumber, int WorkPartNumber, int WindowsPartNumber)? createdFelixWorkInfo = null;
        long? createdFelixWorkOffset = null;

        try
        {
            progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, "Resolving FelixPE boot image", 10));
            (peMount, var bootWimSrc, var bootSdiSrc) = await ResolvePeFilesAsync(peSource, cancellationToken);

            var targetWindowsLetter = ResolveTargetWindowsLetter(snapshot, request.Target);
            var sourceLetter = ExtractDriveLetter(request.Source.ImagePath);
            var copySourceToStaging = targetWindowsLetter is char tw
                && sourceLetter is char sl
                && tw == sl
                && File.Exists(request.Source.ImagePath);

            var sourceCopyBytes = copySourceToStaging ? new FileInfo(request.Source.ImagePath).Length : 0L;
            var requiredBytes = new FileInfo(bootWimSrc).Length
                + new FileInfo(bootSdiSrc).Length
                + sourceCopyBytes
                + 512L * 1024 * 1024;
            requiredBytes = Math.Max(requiredBytes, 1_500_000_000L);

            var candidates = BuildStagingCandidates(snapshot, request.Target, sourceLetter);
            var (targetDiskFacts, targetWinFacts) = BuildTargetDiskAndPartitionFacts(snapshot, request.Target);
            var decision = DesktopHandoffPlanner.PlanStagingOrShrink(
                candidates,
                targetDiskFacts,
                targetWinFacts,
                request.Target.Mode,
                requiredBytes);
            if (!decision.Succeeded)
            {
                throw new InvalidOperationException(decision.Error ?? "No eligible staging volume is available.");
            }

            char stagingLetter;
            if (decision.RequiresFelixWorkShrink)
            {
                progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, "Creating temporary FELIXWORK partition", 16));
                var workLetter = AllocateFreeDriveLetter(snapshot);
                var shrinkScript = InstallDiskLayoutBuilder.CreateFelixWorkShrinkScript(
                    decision.ShrinkDiskNumber!.Value,
                    decision.ShrinkPartitionNumber!.Value,
                    decision.ShrinkSizeMiB,
                    workLetter);
                await DiskPart.RunAsync(shrinkScript, TimeSpan.FromMinutes(3));
                await WaitForVolumeRootAsync(workLetter, cancellationToken);
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

            progress?.Report(new(InstallRunStage.StageDrivers, InstallStageState.Running, "Staging drivers, network, and First Boot payloads", 22));
            var enginePath = WindowSetupEngineLocator.Find();
            var stagingPlatform = new WindowSetupInstallationPlatform(
                enginePath,
                driverStore,
                networkBackup,
                firstBootStaging: firstBootStaging);
            var stagedDrivers = await stagingPlatform.StageDriversAndNetworkAsync(request, sessionDir, progress, cancellationToken);
            var stagedFirstBoot = stagingPlatform.LastStagedFirstBoot;

            var peDir = Path.Combine(sessionDir, "pe");
            Directory.CreateDirectory(peDir);
            var stagedBootWim = Path.Combine(peDir, "boot.wim");
            var stagedBootSdi = Path.Combine(peDir, "boot.sdi");

            progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, "Copying FelixPE image", 42));
            File.Copy(bootWimSrc, stagedBootWim, overwrite: true);
            File.Copy(bootSdiSrc, stagedBootSdi, overwrite: true);
            ClearReadOnlyAttribute(stagedBootWim);
            ClearReadOnlyAttribute(stagedBootSdi);

            var patchedBootCmd = Path.Combine(AppContext.BaseDirectory, "Tools", "felixpe", "felixpe-boot.cmd");
            if (!File.Exists(patchedBootCmd))
            {
                throw new FileNotFoundException("Bundled felixpe-boot.cmd was not found.", patchedBootCmd);
            }

            progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, "Configuring FelixPE auto-resume", 60));
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

            progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, "Staging Felix WinDeploy into FelixPE session", 74));
            var appDestDir = Path.Combine(sessionDir, "app");
            CopyDirectoryFiltered(AppContext.BaseDirectory, appDestDir);

            string? relAnswerPath = null;
            if (!string.IsNullOrWhiteSpace(request.AutounattendPath) && File.Exists(request.AutounattendPath))
            {
                var stagedDir = Path.Combine(sessionDir, "staged");
                Directory.CreateDirectory(stagedDir);
                var unattendDest = Path.Combine(stagedDir, "unattend.xml");
                File.Copy(request.AutounattendPath, unattendDest, overwrite: true);
                relAnswerPath = Path.GetRelativePath(sessionDir, unattendDest);
            }
            else if (request.Options?.GeneratedUnattend is { } generatedSpec)
            {
                var stagedDir = Path.Combine(sessionDir, "staged");
                Directory.CreateDirectory(stagedDir);
                var unattendDest = Path.Combine(stagedDir, "unattend.xml");
                var spec = generatedSpec with
                {
                    BypassMicrosoftAccount = (request.Options ?? new()).BypassMicrosoftAccount
                };
                await File.WriteAllTextAsync(unattendDest, UnattendGenerator.Generate(spec), CancellationToken.None);
                relAnswerPath = Path.GetRelativePath(sessionDir, unattendDest);
            }

            string? relSourcePath = null;
            SourcePartitionSignature? extSignature = null;
            if (copySourceToStaging)
            {
                var sourceDestDir = Path.Combine(sessionDir, "source");
                Directory.CreateDirectory(sourceDestDir);
                var destImg = Path.Combine(sourceDestDir, Path.GetFileName(request.Source.ImagePath));
                File.Copy(request.Source.ImagePath, destImg, overwrite: true);
                if (SourceImages.KindOf(request.Source.ImagePath) == SourceImageKind.Swm)
                {
                    var srcDir = Path.GetDirectoryName(request.Source.ImagePath)!;
                    var prefix = Path.GetFileNameWithoutExtension(request.Source.ImagePath);
                    foreach (var swm in Directory.EnumerateFiles(srcDir, $"{prefix}*.swm"))
                    {
                        File.Copy(swm, Path.Combine(sourceDestDir, Path.GetFileName(swm)), overwrite: true);
                    }
                }
                relSourcePath = Path.GetRelativePath(sessionDir, destImg);
            }
            else
            {
                extSignature = BuildSourceSignature(snapshot, request.Source.ImagePath);
            }

            if (snapshot.Disks.SelectMany(d => d.Partitions).Any(p => p.IsBoot && p.Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase)))
            {
                var manageBde = SystemTools.Find("manage-bde.exe");
                var bootLetter = snapshot.Disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.IsBoot && !string.IsNullOrWhiteSpace(p.Letter))?.Letter;
                if (!string.IsNullOrEmpty(manageBde) && !string.IsNullOrWhiteSpace(bootLetter))
                {
                    await SystemTools.RunAsync(manageBde, ["-protectors", "-disable", $"{bootLetter.TrimEnd(':')}:", "-rc", "2"]);
                }
            }

            progress?.Report(new(InstallRunStage.ConfigureBoot, InstallStageState.Running, "Registering FelixPE boot entry", 88));
            var osId = "{" + Guid.NewGuid() + "}";
            var deviceId = "{" + Guid.NewGuid() + "}";
            var firmware = FirmwareDetector.Current();
            var bcdedit = RequireTool(SystemTools.BcdEdit);
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

            Dictionary<RemoteSupportAppKind, string>? relRemote = null;
            if (stagedFirstBoot is { StagedRemoteInstallers.Count: > 0 })
            {
                relRemote = stagedFirstBoot.StagedRemoteInstallers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => Path.GetRelativePath(sessionDir, kvp.Value));
            }

            if (request.Options?.RescueUsbDiskNumber is int usbDiskNumber)
            {
                var usbDisk = snapshot.Disks.FirstOrDefault(d => d.Number == usbDiskNumber);
                if (usbDisk is not null)
                {
                    progress?.Report(new(InstallRunStage.PrepareTarget, InstallStageState.Running, $"Preparing Rescue USB on Disk {usbDiskNumber}", 92));
                    var usbFacts = NativeTargetSource.Map(usbDisk);
                    var protectedLetters = SetupFileLetters.Of(request.Source.ImagePath, AppContext.BaseDirectory, request.AutounattendPath);
                    var candidate = RescueUsbPlanner.EvaluateUsbDisk(usbFacts, protectedLetters);
                    if (candidate.IsEligible)
                    {
                        await new NativeRescueUsbService().CreateBootRescueUsbAsync(candidate, request.Source.ImagePath, null, cancellationToken);
                    }
                }
            }

            var session = new DesktopHandoffSession(
                SchemaVersion: 1,
                SessionId: Guid.NewGuid().ToString("N"),
                CreatedUtc: DateTime.UtcNow,
                Request: request,
                RelativeSourceImagePath: relSourcePath,
                ExternalSourceSignature: extSignature,
                RelativeAnswerFilePath: relAnswerPath,
                RelativeStagedDriversDir: stagedDrivers?.BackedUpDriversDirectory is { } dDir ? Path.GetRelativePath(sessionDir, dDir) : null,
                RelativeStagedWifiFile: stagedDrivers?.WifiProfilePath is { } wPath ? Path.GetRelativePath(sessionDir, wPath) : null,
                RelativeStagedPrinterFile: stagedDrivers?.PrinterBackupPath is { } pPath ? Path.GetRelativePath(sessionDir, pPath) : null,
                StagedDriverCount: stagedDrivers?.StagedDriverCount ?? 0,
                RelativeStagedRemoteInstallers: relRemote,
                RelativeStagedFirstDesktopSource: stagedFirstBoot?.StagedFirstDesktopSourcePath is { } fPath ? Path.GetRelativePath(sessionDir, fPath) : null,
                BcdOsId: osId,
                BcdDeviceId: deviceId,
                AutoRebootOnComplete: request.Options?.RestartWhenComplete ?? true,
                CreatedFelixWork: createdFelixWorkInfo is not null,
                FelixWorkOffset: createdFelixWorkOffset,
                Recording: request.Options?.Recording,
                RescueUsbDiskNumber: request.Options?.RescueUsbDiskNumber);

            var sessionFile = Path.Combine(sessionDir, "session.json");
            await File.WriteAllTextAsync(sessionFile, session.ToJson(), CancellationToken.None);
            progress?.Report(new(InstallRunStage.Completed, InstallStageState.Completed, "Ready to reboot into FelixPE", 100));

            return new PreparedDesktopHandoff(sessionDir, sessionFile, stagingLetter, osId, deviceId, session);
        }
        catch
        {
            await RollbackAsync(createdOsId, createdDeviceId, createdSessionDir, createdFelixWorkInfo);
            throw;
        }
        finally
        {
            peMount?.Dispose();
        }
    }

    public async Task CancelPreparedHandoffAsync(
        PreparedDesktopHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        (int, int, int)? workInfo = null;
        if (handoff.Session.CreatedFelixWork)
        {
            workInfo = await FindFelixWorkPartitionsAsync(handoff.Session, cancellationToken);
        }

        await RollbackAsync(handoff.BcdOsId, handoff.BcdDeviceId, handoff.SessionDirectory, workInfo);
    }

    public async Task RollbackToOldWindowsFromWinPeAsync(
        DesktopHandoffSession session,
        string sessionFilePath,
        CancellationToken cancellationToken = default)
    {
        var systemLetter = !string.IsNullOrWhiteSpace(session.Request.Target.EfiSystemPartition.AccessPath)
            ? char.ToUpperInvariant(session.Request.Target.EfiSystemPartition.AccessPath[0])
            : '\0';
        await CompleteWinPeResumeCleanupAsync(session, sessionFilePath, systemLetter, session.Request.Target.Firmware, cancellationToken);
        var sessionDir = Path.GetDirectoryName(Path.GetFullPath(sessionFilePath));
        if (!string.IsNullOrWhiteSpace(sessionDir) && Directory.Exists(sessionDir))
        {
            try
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    public async Task CompleteWinPeResumeCleanupAsync(
        DesktopHandoffSession session,
        string sessionFilePath,
        char systemLetter,
        FirmwareMode firmware,
        CancellationToken cancellationToken = default)
    {
        char? tempMountedLetter = null;
        var resolvedLetter = char.IsLetter(systemLetter) ? char.ToUpperInvariant(systemLetter) : (char?)null;
        var storeRelative = firmware == FirmwareMode.UefiGpt
            ? @"EFI\Microsoft\Boot\BCD"
            : @"Boot\BCD";

        try
        {
            if (resolvedLetter is null || !File.Exists($@"{resolvedLetter}:\{storeRelative}"))
            {
                var mounted = await TryFindOrMountSystemPartitionAsync(session.Request.Target, cancellationToken);
                if (mounted.Letter is char foundLetter)
                {
                    resolvedLetter = foundLetter;
                    if (mounted.TemporaryMount)
                    {
                        tempMountedLetter = foundLetter;
                    }
                }
            }

            if (resolvedLetter is char activeLetter)
            {
                var storePath = $@"{activeLetter}:\{storeRelative}";
                if (File.Exists(storePath))
                {
                    var bcdedit = SystemTools.Find(SystemTools.BcdEdit);
                    if (!string.IsNullOrEmpty(bcdedit))
                    {
                        foreach (var args in DesktopHandoffPlanner.BuildBcdCleanupCommands(session.BcdOsId, session.BcdDeviceId, storePath))
                        {
                            try
                            {
                                await SystemTools.RunAsync(bcdedit, args);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (tempMountedLetter is char letterToRemove)
            {
                try
                {
                    await DiskPart.RunAsync($"select volume {letterToRemove}\nremove letter={letterToRemove} noerr\n", TimeSpan.FromMinutes(1));
                }
                catch
                {
                }
            }
        }

        if (File.Exists(sessionFilePath))
        {
            var completedPath = Path.Combine(
                Path.GetDirectoryName(sessionFilePath)!,
                "session.completed.json");
            File.Move(sessionFilePath, completedPath, overwrite: true);
        }

        if (session.CreatedFelixWork)
        {
            var workInfo = await FindFelixWorkPartitionsAsync(session, cancellationToken);
            if (workInfo is { } info)
            {
                try
                {
                    await DiskPart.RunAsync(
                        InstallDiskLayoutBuilder.CreateRemoveFelixWorkAndExtendScript(info.DiskNumber, info.WorkPartNumber, info.WindowsPartNumber),
                        TimeSpan.FromMinutes(2));
                }
                catch
                {
                }
            }
        }
    }

    private static async Task<(char? Letter, bool TemporaryMount)> TryFindOrMountSystemPartitionAsync(
        InstallTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
            var disk = snapshot.Disks.FirstOrDefault(d =>
                d.Number == target.DiskNumber
                || ((string.IsNullOrEmpty(target.DiskId) || d.Id == target.DiskId)
                    && (string.IsNullOrEmpty(target.DiskSerial) || d.Serial == target.DiskSerial)
                    && (target.DiskSize <= 0 || d.Size == target.DiskSize)));
            if (disk is null)
            {
                return (null, false);
            }

            var sysPart = disk.Partitions.FirstOrDefault(p =>
                !p.Unallocated &&
                p.Offset == target.EfiSystemPartition.Offset &&
                p.Size == target.EfiSystemPartition.Size)
                ?? disk.Partitions.FirstOrDefault(p =>
                    !p.Unallocated &&
                    (p.GptType.Trim('{', '}').Equals("c12a7328-f81f-11d2-ba4b-00a0c93ec93b", StringComparison.OrdinalIgnoreCase)
                     || p.IsActive));
            if (sysPart is null)
            {
                return (null, false);
            }

            if (!string.IsNullOrWhiteSpace(sysPart.Letter))
            {
                return (char.ToUpperInvariant(sysPart.Letter.TrimEnd(':')[0]), false);
            }

            var used = snapshot.OccupiedLetters.Select(char.ToUpperInvariant).ToHashSet();
            for (var c = 'S'; c <= 'Z'; c++)
            {
                if (!used.Contains(c))
                {
                    await DiskPart.RunAsync(
                        $"select disk {disk.Number}\nselect partition {sysPart.Number}\nassign letter={c} noerr\n",
                        TimeSpan.FromMinutes(1));
                    if (Directory.Exists($@"{c}:\"))
                    {
                        return (c, true);
                    }
                    break;
                }
            }
        }
        catch
        {
        }

        return (null, false);
    }

    public static string ResolveResumeSourcePath(DesktopHandoffSession session, string sessionDir)
    {
        if (DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, session.RelativeSourceImagePath) is { } stagedSrc
            && File.Exists(stagedSrc))
        {
            return stagedSrc;
        }

        if (session.ExternalSourceSignature is { } sig)
        {
            var snapshot = NativeStorageReader.Read();
            var matched = snapshot.Disks
                .Where(d => (string.IsNullOrEmpty(sig.DiskId) || d.Id == sig.DiskId)
                    && (string.IsNullOrEmpty(sig.DiskSerial) || d.Serial == sig.DiskSerial))
                .SelectMany(d => d.Partitions)
                .FirstOrDefault(p => p.Offset == sig.PartitionOffset
                    && p.Size == sig.PartitionSize
                    && !string.IsNullOrWhiteSpace(p.Letter));

            if (matched is not null)
            {
                var candidate = Path.Combine($"{char.ToUpperInvariant(matched.Letter.TrimEnd(':')[0])}:\\", sig.RelativePathFromRoot);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return session.Request.Source.ImagePath;
    }

    public static (StagedDriverArtifacts? Drivers, StagedFirstBootArtifacts? FirstBoot) RehydrateStagedArtifacts(
        DesktopHandoffSession session,
        string sessionDir)
    {
        var driversDir = DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, session.RelativeStagedDriversDir);
        var wifiPath = DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, session.RelativeStagedWifiFile);
        var printerPath = DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, session.RelativeStagedPrinterFile);
        var hasAnyDriver = driversDir is not null || wifiPath is not null || printerPath is not null || session.StagedDriverCount > 0;

        StagedDriverArtifacts? drivers = hasAnyDriver
            ? new StagedDriverArtifacts(driversDir, wifiPath, printerPath, session.StagedDriverCount)
            : null;

        var remoteMap = new Dictionary<RemoteSupportAppKind, string>();
        if (session.RelativeStagedRemoteInstallers is not null)
        {
            foreach (var (kind, rel) in session.RelativeStagedRemoteInstallers)
            {
                if (DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, rel) is { } abs && File.Exists(abs))
                {
                    remoteMap[kind] = abs;
                }
            }
        }

        var firstDesktop = DesktopHandoffPlanner.ResolveSessionRelativePath(sessionDir, session.RelativeStagedFirstDesktopSource);
        var firstBoot = remoteMap.Count > 0 || firstDesktop is not null
            ? new StagedFirstBootArtifacts(Path.Combine(sessionDir, "staged", "FirstBoot"), remoteMap, firstDesktop)
            : null;

        return (drivers, firstBoot);
    }

    internal static async Task<(IsoMountLease? Mount, string BootWim, string BootSdi)> ResolvePeFilesAsync(
        FelixPeSource peSource,
        CancellationToken cancellationToken)
    {
        if (!peSource.IsIso)
        {
            if (string.IsNullOrWhiteSpace(peSource.BootWimPath) || !File.Exists(peSource.BootWimPath)
                || string.IsNullOrWhiteSpace(peSource.BootSdiPath) || !File.Exists(peSource.BootSdiPath))
            {
                throw new FileNotFoundException("FelixPE boot.wim or boot.sdi could not be found.", peSource.DisplayPath);
            }

            return (null, peSource.BootWimPath, peSource.BootSdiPath);
        }

        var mount = await IsoMountLease.AttachAsync(peSource.DisplayPath, cancellationToken);
        var bootWim = new[]
            {
                Path.Combine(mount.RootPath, "sources", "boot.wim"),
                Path.Combine(mount.RootPath, "boot.wim")
            }
            .FirstOrDefault(File.Exists);
        var bootSdi = new[]
            {
                Path.Combine(mount.RootPath, "boot", "boot.sdi"),
                Path.Combine(mount.RootPath, "boot.sdi")
            }
            .FirstOrDefault(File.Exists);

        if (bootWim is null || bootSdi is null)
        {
            mount.Dispose();
            throw new InvalidOperationException("The FelixPE ISO does not contain sources\\boot.wim and boot\\boot.sdi.");
        }

        return (mount, bootWim, bootSdi);
    }

    private static List<StagingVolumeCandidate> BuildStagingCandidates(
        StorageSnapshot snapshot,
        InstallTarget target,
        char? sourceLetter)
    {
        var list = new List<StagingVolumeCandidate>();
        foreach (var disk in snapshot.Disks)
        {
            var isRemovable = disk.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase);
            var isOnTargetDisk = disk.Number == target.DiskNumber;

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

                var isTargetWin = target.Mode == InstallTargetMode.ExistingPartition
                    && isOnTargetDisk
                    && part.Number == target.WindowsPartition.Number
                    && part.Offset == target.WindowsPartition.Offset;

                list.Add(new StagingVolumeCandidate(
                    letter,
                    part.Free,
                    part.FileSystem,
                    part.Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase),
                    isRemovable,
                    isOnTargetDisk,
                    isTargetWin,
                    sourceLetter == letter));
            }
        }

        return list;
    }

    private static SourcePartitionSignature? BuildSourceSignature(StorageSnapshot snapshot, string imagePath)
    {
        var letter = ExtractDriveLetter(imagePath);
        if (letter is null || !Path.IsPathRooted(imagePath))
        {
            return null;
        }

        foreach (var disk in snapshot.Disks)
        {
            foreach (var part in disk.Partitions)
            {
                if (!string.IsNullOrWhiteSpace(part.Letter)
                    && char.ToUpperInvariant(part.Letter.TrimEnd(':')[0]) == letter)
                {
                    var root = $@"{letter}:\";
                    var rel = Path.GetRelativePath(root, Path.GetFullPath(imagePath));
                    return new SourcePartitionSignature(disk.Number, disk.Id, disk.Serial, part.Offset, part.Size, rel);
                }
            }
        }

        return null;
    }

    private static char? ResolveTargetWindowsLetter(StorageSnapshot snapshot, InstallTarget target)
    {
        if (target.Mode != InstallTargetMode.ExistingPartition)
        {
            return null;
        }

        var part = snapshot.Disks
            .Where(d => d.Number == target.DiskNumber)
            .SelectMany(d => d.Partitions)
            .FirstOrDefault(p => p.Number == target.WindowsPartition.Number && p.Offset == target.WindowsPartition.Offset);

        return part is not null && !string.IsNullOrWhiteSpace(part.Letter)
            ? char.ToUpperInvariant(part.Letter.TrimEnd(':')[0])
            : ExtractDriveLetter(target.WindowsPartition.AccessPath);
    }

    private static char? ExtractDriveLetter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':')
        {
            return null;
        }

        var c = char.ToUpperInvariant(path[0]);
        return c is >= 'A' and <= 'Z' ? c : null;
    }

    internal static void ClearReadOnlyAttribute(string filePath)
    {
        var attrs = File.GetAttributes(filePath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
        }
    }

    internal static void CopyDirectoryFiltered(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(subDir);
            if (name.Equals("Logs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectoryFiltered(subDir, Path.Combine(destinationDir, name));
        }
    }

    internal static async Task RollbackAsync(
        string? osId,
        string? deviceId,
        string? sessionDir,
        (int DiskNumber, int WorkPartNumber, int WindowsPartNumber)? felixWorkInfo = null)
    {
        var bcdedit = SystemTools.Find(SystemTools.BcdEdit);
        if (!string.IsNullOrEmpty(bcdedit))
        {
            foreach (var args in DesktopHandoffPlanner.BuildBcdCleanupCommands(osId, deviceId))
            {
                try
                {
                    await SystemTools.RunAsync(bcdedit, args);
                }
                catch
                {
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sessionDir) && Directory.Exists(sessionDir))
        {
            try
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            catch
            {
            }
        }

        if (felixWorkInfo is { } info)
        {
            try
            {
                await DiskPart.RunAsync(
                    InstallDiskLayoutBuilder.CreateRemoveFelixWorkAndExtendScript(info.DiskNumber, info.WorkPartNumber, info.WindowsPartNumber),
                    TimeSpan.FromMinutes(2));
            }
            catch
            {
            }
        }
    }

    private static async Task<(int DiskNumber, int WorkPartNumber, int WindowsPartNumber)?> FindFelixWorkPartitionsAsync(
        DesktopHandoffSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
            var target = session.Request.Target;
            var disk = snapshot.Disks.FirstOrDefault(d =>
                d.Number == target.DiskNumber
                || ((string.IsNullOrEmpty(target.DiskId) || d.Id == target.DiskId)
                    && (string.IsNullOrEmpty(target.DiskSerial) || d.Serial == target.DiskSerial)
                    && (target.DiskSize <= 0 || d.Size == target.DiskSize)));
            if (disk is null)
            {
                return null;
            }

            var workPart = disk.Partitions.FirstOrDefault(p =>
                !p.Unallocated &&
                ((session.FelixWorkOffset is long off && p.Offset == off)
                 || p.Label.Equals("FELIXWORK", StringComparison.OrdinalIgnoreCase)));
            var winPart = disk.Partitions.FirstOrDefault(p =>
                !p.Unallocated &&
                p.Offset == target.WindowsPartition.Offset &&
                p.Size == target.WindowsPartition.Size)
                ?? disk.Partitions.FirstOrDefault(p => !p.Unallocated && p.Number == target.WindowsPartition.Number);

            return workPart is not null && winPart is not null
                ? (disk.Number, workPart.Number, winPart.Number)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static (TargetDiskFacts? Disk, TargetPartitionFacts? Partition) BuildTargetDiskAndPartitionFacts(
        StorageSnapshot snapshot,
        InstallTarget target)
    {
        var disk = snapshot.Disks.FirstOrDefault(d => d.Number == target.DiskNumber);
        if (disk is null)
        {
            return (null, null);
        }

        var facts = NativeTargetSource.Map(disk);
        var part = facts.Partitions.FirstOrDefault(p =>
            !p.IsUnallocated &&
            p.Number == target.WindowsPartition.Number &&
            p.Offset == target.WindowsPartition.Offset);
        return (facts, part);
    }

    internal static char AllocateFreeDriveLetter(StorageSnapshot snapshot)
    {
        var used = snapshot.OccupiedLetters.Select(char.ToUpperInvariant).ToHashSet();
        foreach (var c in new[] { 'K', 'J', 'W', 'V', 'Q' }.Concat(Enumerable.Range('D', 23).Select(i => (char)i)))
        {
            var upper = char.ToUpperInvariant(c);
            if (!used.Contains(upper))
            {
                return upper;
            }
        }

        throw new InvalidOperationException("No free drive letter is available for temporary FELIXWORK partition.");
    }

    internal static async Task WaitForVolumeRootAsync(char letter, CancellationToken cancellationToken)
    {
        var root = $@"{char.ToUpperInvariant(letter)}:\";
        for (var i = 0; i < 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(root))
            {
                return;
            }
            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Temporary staging partition {root} did not mount in time.");
    }

    internal static string RequireTool(string fileName) =>
        SystemTools.Find(fileName)
        ?? throw new FileNotFoundException($"Required system tool was not found: {fileName}", fileName);
}
