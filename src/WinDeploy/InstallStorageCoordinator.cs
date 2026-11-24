using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup;

public sealed class InstallStorageCoordinator
{
    private readonly List<string> dynamicTemporaryAccessPaths = [];

    public async Task<PreparedInstallation> PrepareAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress)
    {
        var snapshot = await Task.Run(NativeStorageReader.Read);
        StorageSnapshotRules.Check(snapshot);
        var disk = ResolveReviewedDisk(snapshot, request.Target);
        RefuseSourceOverlap(disk, request.Target, request.Source.ImagePath, request.AutounattendPath);
        var temporaryFiles = new List<string>();
        request = CopyAnswerFile(request, temporaryFiles);
        var createRecovery = (request.Options ?? new()).CreateRecovery;
        var letters = AllocateLetters(snapshot, request.Target, createRecovery);

        try
        {
            if (request.Target.Mode == InstallTargetMode.EraseDisk)
            {
                if (disk.Size < InstallDiskLayoutBuilder.MinimumDiskBytes)
                {
                    throw new InvalidOperationException("Erase disk requires at least 32 GB.");
                }
                progress?.Report(new(
                    InstallRunStage.PrepareTarget,
                    InstallStageState.Running,
                    "Creating the new partition layout",
                    16));
                var layout = InstallDiskLayoutBuilder.Create(
                    disk.Number,
                    request.Target.Firmware,
                    letters.System,
                    letters.Windows,
                    letters.Recovery,
                    createRecovery);
                await File.WriteAllTextAsync(
                    Path.Combine(runDirectory, "diskpart-layout.txt"),
                    layout.Script,
                    CancellationToken.None);
                await DiskPart.RunAsync(layout.Script);
            }
            else
            {
                await VerifyExistingTargetsAsync(disk, request.Target);
                progress?.Report(new(
                    InstallRunStage.PrepareTarget,
                    InstallStageState.Running,
                    "Formatting the Windows partition",
                    16));
                await FormatExistingWindowsAsync(disk, request.Target.WindowsPartition, letters.Windows);
                disk = ResolveReviewedDisk(
                    await Task.Run(NativeStorageReader.Read),
                    request.Target);
                var sharedSystem = request.Target.Firmware == FirmwareMode.BiosMbr &&
                    request.Target.WindowsPartition.Identity.Equals(request.Target.EfiSystemPartition.Identity, StringComparison.OrdinalIgnoreCase);
                if (!sharedSystem)
                {
                    await EnsureAccessPathAsync(disk, request.Target.EfiSystemPartition, letters.System);
                }
                if (request.Target.Firmware == FirmwareMode.BiosMbr)
                {
                    await MarkSystemPartitionActiveAsync(
                        disk,
                        request.Target.EfiSystemPartition);
                }
                if (createRecovery &&
                    request.Target.RecoveryPartition.Identity != "none")
                {
                    disk = ResolveReviewedDisk(
                        await Task.Run(NativeStorageReader.Read),
                        request.Target);
                    await PrepareExistingRecoveryAsync(
                        disk,
                        request.Target.RecoveryPartition,
                        letters.Recovery!.Value);
                }
            }

            var fresh = await WaitForPreparedDiskAsync(
                disk.Number,
                disk.Size,
                letters,
                createRecovery);
            var preparedTarget = BuildPreparedTarget(request.Target, fresh, letters);
            return new PreparedInstallation(
                request with { Target = preparedTarget },
                runDirectory,
                letters.Temporary.Select(letter => $"{letter}:").ToArray(),
                temporaryFiles);
        }
        catch
        {
            DeleteTemporaryFiles(temporaryFiles);
            throw;
        }
    }

    public async Task<char> ShrinkWindowsForRecoveryAsync(
        PreparedInstallation installation,
        int requiredSizeMiB)
    {
        var snapshot = await Task.Run(NativeStorageReader.Read);
        var disk = snapshot.Disks.SingleOrDefault(candidate =>
            candidate.Number == installation.Request.Target.DiskNumber)
            ?? throw new InvalidOperationException("Target disk was not found when creating Recovery partition.");

        var windowsPartition = ResolveReviewedPartition(disk, installation.Request.Target.WindowsPartition);
        var used = snapshot.OccupiedLetters.Select(char.ToUpperInvariant).ToHashSet();
        char? chosen = null;
        foreach (var candidate in new[] { 'R', 'Q' }.Concat(Enumerable.Range('D', 23).Select(n => (char)n)))
        {
            var upper = char.ToUpperInvariant(candidate);
            if (!used.Contains(upper))
            {
                chosen = upper;
                break;
            }
        }

        if (chosen is not char recoveryLetter)
        {
            throw new InvalidOperationException("No free drive letter is available for the new Recovery partition.");
        }

        var script = InstallDiskLayoutBuilder.CreateShrinkRecoveryScript(
            disk.Number,
            windowsPartition.Number,
            installation.Request.Target.Firmware,
            requiredSizeMiB,
            recoveryLetter);
        await File.WriteAllTextAsync(
            Path.Combine(installation.RunDirectory, "diskpart-shrink-recovery.txt"),
            script,
            CancellationToken.None);
        await DiskPart.RunAsync(script);

        dynamicTemporaryAccessPaths.Add($"{recoveryLetter}:");
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (Directory.Exists($@"{recoveryLetter}:\"))
            {
                return recoveryLetter;
            }
            await Task.Delay(250);
        }

        throw new TimeoutException($"The newly created Recovery partition ({recoveryLetter}:) did not mount in time.");
    }

    public async Task CleanupAsync(PreparedInstallation installation, bool succeeded)
    {
        foreach (var accessPath in installation.TemporaryAccessPaths.Concat(dynamicTemporaryAccessPaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (accessPath.Length != 2)
            {
                continue;
            }
            var letter = accessPath[0];
            await DiskPart.RunAsync(
                $"select volume {letter}\nremove letter={letter} noerr\n",
                TimeSpan.FromMinutes(1));
        }
        dynamicTemporaryAccessPaths.Clear();
        DeleteTemporaryFiles(installation.TemporaryFiles);
    }

    private static StorageDisk ResolveReviewedDisk(
        StorageSnapshot snapshot,
        InstallTarget target)
    {
        var disk = snapshot.Disks.SingleOrDefault(candidate =>
            candidate.Number == target.DiskNumber &&
            candidate.Id == target.DiskId &&
            candidate.Serial == target.DiskSerial &&
            candidate.Size == target.DiskSize &&
            candidate.Style == target.DiskStyle);

        if (disk is null && !string.IsNullOrEmpty(target.DiskId))
        {
            disk = snapshot.Disks.SingleOrDefault(candidate =>
                candidate.Id == target.DiskId &&
                candidate.Serial == target.DiskSerial &&
                candidate.Size == target.DiskSize &&
                candidate.Style == target.DiskStyle);
        }

        if (disk is null)
        {
            throw new InvalidOperationException("The target disk identity or layout changed after review.");
        }
        if (disk.HoldsPeMedia || disk.Partitions.Any(partition => partition.IsBoot))
        {
            throw new InvalidOperationException("The disk running FelixPE or Windows cannot be used as the target.");
        }
        if (disk.IsOffline || disk.IsReadOnly || disk.IsDynamic)
        {
            throw new InvalidOperationException("The target disk is offline, read-only, or dynamic.");
        }
        if (!PeEnvironment.IsWinPe && !IsVirtualDisk(disk.Model))
        {
            throw new InvalidOperationException(
                "Physical-disk installation is enabled only while Felix WinDeploy is running in FelixPE.");
        }
        return disk;
    }

    private static bool IsVirtualDisk(string model) =>
        model.Equals("Msft Virtual Disk", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("Microsoft Virtual Disk", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("Virtual HD", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("Virtual HD ATA Device", StringComparison.OrdinalIgnoreCase);

    private static void RefuseSourceOverlap(StorageDisk disk, InstallTarget target, string imagePath, string? answerPath)
    {
        var letters = SetupFileLetters.Of(imagePath, AppContext.BaseDirectory, answerPath);
        if (target.Mode == InstallTargetMode.ExistingPartition)
        {
            var winPartition = disk.Partitions.SingleOrDefault(p =>
                p.Number == target.WindowsPartition.Number &&
                p.Offset == target.WindowsPartition.Offset);
            if (winPartition is not null &&
                !string.IsNullOrWhiteSpace(winPartition.Letter) &&
                letters.Contains(char.ToUpperInvariant(winPartition.Letter.TrimEnd(':')[0])))
            {
                throw new InvalidOperationException("The selected target Windows partition contains the Windows source or setup files.");
            }
            return;
        }

        if (disk.Partitions.Any(partition =>
                !string.IsNullOrWhiteSpace(partition.Letter) &&
                letters.Contains(char.ToUpperInvariant(partition.Letter[0]))))
        {
            throw new InvalidOperationException("The selected target disk contains the Windows source or setup files.");
        }
    }

    private static AccessLetters AllocateLetters(
        StorageSnapshot snapshot,
        InstallTarget target,
        bool createRecovery)
    {
        var used = snapshot.OccupiedLetters
            .Select(char.ToUpperInvariant)
            .ToHashSet();
        var temporary = new List<char>();

        char Pick(PartitionTarget partition, params char[] preferred)
        {
            if (partition.AccessPath.Length >= 2 &&
                partition.AccessPath[1] == ':')
            {
                return char.ToUpperInvariant(partition.AccessPath[0]);
            }
            foreach (var value in preferred.Concat(Enumerable.Range('D', 23).Select(number => (char)number)))
            {
                var letter = char.ToUpperInvariant(value);
                if (used.Add(letter))
                {
                    temporary.Add(letter);
                    return letter;
                }
            }
            throw new InvalidOperationException("No free drive letter is available.");
        }

        var empty = new PartitionTarget("", "");
        var windows = Pick(
            target.Mode == InstallTargetMode.EraseDisk ? empty : target.WindowsPartition,
            'W', 'V');
        var sharedSystem = target.Mode == InstallTargetMode.ExistingPartition &&
            target.Firmware == FirmwareMode.BiosMbr &&
            target.WindowsPartition.Identity.Equals(target.EfiSystemPartition.Identity, StringComparison.OrdinalIgnoreCase);
        var system = sharedSystem
            ? windows
            : Pick(
                target.Mode == InstallTargetMode.EraseDisk ? empty : target.EfiSystemPartition,
                'S', 'T');
        char? recovery = null;
        if (createRecovery &&
            (target.Mode == InstallTargetMode.EraseDisk ||
             target.RecoveryPartition.Identity != "none"))
        {
            recovery = Pick(
                target.Mode == InstallTargetMode.EraseDisk ? empty : target.RecoveryPartition,
                'R', 'Q');
        }
        return new(system, windows, recovery, temporary);
    }

    private static async Task FormatExistingWindowsAsync(
        StorageDisk disk,
        PartitionTarget target,
        char letter)
    {
        var partition = ResolveReviewedPartition(disk, target);
        await VerifyDiskPartSelectionAsync(disk, partition);
        await DiskPart.RunAsync(
            DiskPart.SelectPartition(disk.Number, partition.Number) +
            $"format quick fs=ntfs label=Windows override\nassign letter={letter} noerr\n");
    }

    private static async Task VerifyExistingTargetsAsync(
        StorageDisk disk,
        InstallTarget target)
    {
        var partitions = new List<StoragePartition>
        {
            ResolveReviewedPartition(disk, target.WindowsPartition),
            ResolveReviewedPartition(disk, target.EfiSystemPartition)
        };
        if ((target.RecoveryPartition.Identity != "none") &&
            !string.IsNullOrWhiteSpace(target.RecoveryPartition.Identity))
        {
            partitions.Add(ResolveReviewedPartition(disk, target.RecoveryPartition));
        }
        var sharedSystem = target.Firmware == FirmwareMode.BiosMbr &&
            target.WindowsPartition.Identity.Equals(target.EfiSystemPartition.Identity, StringComparison.OrdinalIgnoreCase);
        if (!sharedSystem && partitions.Select(partition => partition.Offset).Distinct().Count() != partitions.Count)
        {
            throw new InvalidOperationException("Windows, EFI/System, and Recovery must use different partitions.");
        }
        foreach (var partition in partitions)
        {
            await VerifyDiskPartSelectionAsync(disk, partition);
        }
    }

    private static async Task PrepareExistingRecoveryAsync(
        StorageDisk disk,
        PartitionTarget target,
        char letter)
    {
        var partition = ResolveReviewedPartition(disk, target);
        await VerifyDiskPartSelectionAsync(disk, partition);
        var type = disk.Style == "GPT"
            ? $"set id=DE94BBA4-06D1-4D40-A16A-BFD50179D6AC\ngpt attributes=0x8000000000000001\n"
            : "set id=27 override\n";
        var formatLine = partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            ? ""
            : "format quick fs=ntfs label=Recovery override\n";
        await DiskPart.RunAsync(
            DiskPart.SelectPartition(disk.Number, partition.Number) +
            type +
            formatLine +
            $"assign letter={letter} noerr\n");
    }

    private static async Task EnsureAccessPathAsync(
        StorageDisk disk,
        PartitionTarget target,
        char letter)
    {
        var partition = ResolveReviewedPartition(disk, target);
        if (target.AccessPath.Length >= 2)
        {
            return;
        }
        await VerifyDiskPartSelectionAsync(disk, partition);
        await DiskPart.RunAsync(
            DiskPart.SelectPartition(disk.Number, partition.Number) +
            $"assign letter={letter}\n");
    }

    private static async Task MarkSystemPartitionActiveAsync(
        StorageDisk disk,
        PartitionTarget target)
    {
        var partition = ResolveReviewedPartition(disk, target);
        if (partition.IsActive)
        {
            return;
        }
        await VerifyDiskPartSelectionAsync(disk, partition);
        await DiskPart.RunAsync(
            DiskPart.SelectPartition(disk.Number, partition.Number) +
            "active\n");
    }

    private static StoragePartition ResolveReviewedPartition(
        StorageDisk disk,
        PartitionTarget target)
    {
        return disk.Partitions.SingleOrDefault(partition =>
                   partition.Number == target.Number &&
                   partition.Offset == target.Offset &&
                   partition.Size == target.Size &&
                   partition.Guid == target.Guid &&
                   partition.VolumeSerial == target.VolumeSerial)
               ?? throw new InvalidOperationException(
                   $"{target.DisplayName} changed after review.");
    }

    private static async Task VerifyDiskPartSelectionAsync(
        StorageDisk disk,
        StoragePartition partition)
    {
        var output = await DiskPart.RunAsync(
            DiskPart.SelectPartition(disk.Number, partition.Number) +
            "detail partition\n",
            TimeSpan.FromMinutes(1));
        if (!DiskPart.ShowsOffset(output, partition.Offset))
        {
            throw new InvalidOperationException("DiskPart selected a different partition than the reviewed target.");
        }
    }

    private static async Task<StorageDisk> WaitForPreparedDiskAsync(
        int diskNumber,
        long size,
        AccessLetters letters,
        bool requireRecovery)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var snapshot = await Task.Run(NativeStorageReader.Read);
            var disk = snapshot.Disks.SingleOrDefault(candidate =>
                candidate.Number == diskNumber && candidate.Size == size);
            if (disk is not null &&
                HasPartition(disk, letters.Windows, "Windows") &&
                (letters.System == letters.Windows || HasPartition(disk, letters.System, "System")) &&
                (!requireRecovery ||
                 letters.Recovery is char recovery &&
                 HasPartition(disk, recovery, "Recovery")))
            {
                return disk;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The prepared target partitions did not appear.");
    }

    private static bool HasPartition(StorageDisk disk, char letter, string label) =>
        disk.Partitions.Any(partition =>
            partition.Letter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase) ||
            partition.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

    private static InstallTarget BuildPreparedTarget(
        InstallTarget reviewed,
        StorageDisk fresh,
        AccessLetters letters)
    {
        PartitionTarget Map(char letter, string label)
        {
            var partition = fresh.Partitions.SingleOrDefault(candidate =>
                candidate.Letter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
                ?? fresh.Partitions.SingleOrDefault(candidate =>
                    candidate.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{label} partition was not found after preparation.");
            return new(
                IdentityOf(fresh, partition),
                $"{letter}: {label}",
                $"{letter}:",
                partition.Number,
                partition.Offset,
                partition.Size,
                partition.Guid,
                partition.VolumeSerial,
                partition.FileSystem,
                partition.Encryption == "Encrypted");
        }

        var windows = Map(letters.Windows, "Windows");
        var sharedSystem = reviewed.Firmware == FirmwareMode.BiosMbr &&
            reviewed.WindowsPartition.Identity.Equals(reviewed.EfiSystemPartition.Identity, StringComparison.OrdinalIgnoreCase);
        var system = sharedSystem ? windows : Map(letters.System, "System");
        var recovery = letters.Recovery is char recoveryLetter
            ? Map(recoveryLetter, "Recovery")
            : new PartitionTarget("none", "Do not use");
        return reviewed with
        {
            DiskIdentity = DiskIdentityOf(fresh),
            DiskNumber = fresh.Number,
            DiskId = fresh.Id,
            DiskSerial = fresh.Serial,
            DiskSize = fresh.Size,
            DiskStyle = fresh.Style,
            WindowsPartition = windows,
            EfiSystemPartition = system,
            RecoveryPartition = recovery
        };
    }

    private static string DiskIdentityOf(StorageDisk disk) =>
        string.Join("|", disk.Number, disk.Id, disk.Serial, disk.Size);

    private static string IdentityOf(StorageDisk disk, StoragePartition partition) =>
        string.Join("|", disk.Number, partition.Offset, partition.Guid, partition.VolumeSerial);

    private static WindowSetupRequest CopyAnswerFile(
        WindowSetupRequest request,
        ICollection<string> temporaryFiles)
    {
        if (string.IsNullOrWhiteSpace(request.AutounattendPath))
        {
            return request;
        }
        var root = Path.Combine(
            Path.GetTempPath(),
            "FelixPE",
            "WindowSetupInputs",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Path.GetFileName(request.AutounattendPath));
        File.Copy(request.AutounattendPath, path, false);
        temporaryFiles.Add(path);
        return request with { AutounattendPath = path };
    }

    private static void DeleteTemporaryFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) &&
                    Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record AccessLetters(
        char System,
        char Windows,
        char? Recovery,
        IReadOnlyList<char> Temporary);
}
