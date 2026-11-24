using System.IO;
using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface IRescueUsbService
{
    Task CreateBootRescueUsbAsync(
        RescueUsbCandidate usb,
        string? windowsSourcePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class NativeRescueUsbService : IRescueUsbService
{
    public async Task CreateBootRescueUsbAsync(
        RescueUsbCandidate usb,
        string? windowsSourcePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usb);
        if (!usb.IsEligible)
        {
            throw new InvalidOperationException(usb.BlockReason ?? "Selected USB drive is not eligible.");
        }

        progress?.Report("Resolving WinPE boot files...");
        var peSource = FelixPeLocator.TryResolve(null, windowsSourcePath)
            ?? throw new InvalidOperationException("No WinPE (FelixPE / Winre.wim) boot image was found on this system.");

        var bcdedit = RequireTool(SystemTools.BcdEdit);
        var engine = WindowSetupEngineLocator.Find();

        IsoMountLease? winMount = null;
        IsoMountLease? peMount = null;
        try
        {
            var bootMgfwSrc = ResolveBootMgfw(windowsSourcePath, out winMount)
                ?? throw new FileNotFoundException("Windows UEFI boot manager (bootmgfw.efi) was not found.");

            (var bootWimSrc, var bootSdiSrc) = await ResolvePeFilesAsync(peSource, cancellationToken, m => peMount = m);

            char fat32Letter;
            if (usb.PreparationMode == RescueUsbMode.FormatDisk)
            {
                progress?.Report($"Formatting Disk {usb.DiskNumber} for UEFI rescue...");
                var snapshot = await Task.Run(NativeStorageReader.Read, cancellationToken);
                fat32Letter = AllocateFreeLetter(snapshot);
                var script = RescueUsbPlanner.BuildFormatUsbScript(usb.DiskNumber, usb.SizeBytes, fat32Letter);
                await DiskPart.RunAsync(script, TimeSpan.FromMinutes(3));
                await WaitForRootAsync(fat32Letter, cancellationToken);
            }
            else
            {
                fat32Letter = usb.ExistingFat32Letter
                    ?? throw new InvalidOperationException("Existing FAT32 partition has no drive letter.");
            }

            var root = $@"{char.ToUpperInvariant(fat32Letter)}:\";
            var rescueDir = Path.Combine(root, "FelixBootRescue");
            var efiBootDir = Path.Combine(root, "EFI", "BOOT");
            var msBootDir = Path.Combine(root, "EFI", "Microsoft", "Boot");
            Directory.CreateDirectory(rescueDir);
            Directory.CreateDirectory(Path.Combine(rescueDir, "Drivers"));
            Directory.CreateDirectory(Path.Combine(rescueDir, "Backup"));
            Directory.CreateDirectory(Path.Combine(rescueDir, "logs"));
            Directory.CreateDirectory(efiBootDir);
            Directory.CreateDirectory(msBootDir);

            progress?.Report("Copying EFI bootloader and WinPE image...");
            File.Copy(bootMgfwSrc, Path.Combine(efiBootDir, "BOOTX64.EFI"), overwrite: true);
            File.Copy(bootMgfwSrc, Path.Combine(msBootDir, "bootmgfw.efi"), overwrite: true);

            var destSdi = Path.Combine(rescueDir, "boot.sdi");
            var destWim = Path.Combine(rescueDir, "boot.wim");
            File.Copy(bootSdiSrc, destSdi, overwrite: true);
            File.Copy(bootWimSrc, destWim, overwrite: true);
            ClearReadOnlyAttribute(destSdi);
            ClearReadOnlyAttribute(destWim);

            progress?.Report("Copying Felix WinDeploy tools...");
            CopyDirectoryFiltered(AppContext.BaseDirectory, Path.Combine(rescueDir, "app"));

            progress?.Report("Patching rescue shell into WinPE...");
            var rescueCmd = Path.Combine(AppContext.BaseDirectory, "Tools", "felixpe", "felixpe-rescue.cmd");
            if (!File.Exists(rescueCmd))
            {
                throw new FileNotFoundException("Bundled felixpe-rescue.cmd was not found.", rescueCmd);
            }

            var tempIni = Path.Combine(rescueDir, "winpeshl.ini.tmp");
            await File.WriteAllTextAsync(
                tempIni,
                "[LaunchApps]\r\n%SystemRoot%\\System32\\cmd.exe, /c %SystemRoot%\\System32\\felixpe-rescue.cmd\r\n",
                cancellationToken);
            try
            {
                var qCmd = rescueCmd.Contains(' ') ? $"\"{rescueCmd}\"" : rescueCmd;
                var qIni = tempIni.Contains(' ') ? $"\"{tempIni}\"" : tempIni;
                var patchCmd = new WimlibCommand(
                    engine,
                    [
                        "update",
                        destWim,
                        "1",
                        $"--command=add {qCmd} \\Windows\\System32\\felixpe-rescue.cmd",
                        $"--command=add {qIni} \\Windows\\System32\\winpeshl.ini"
                    ]);
                var patchRes = await WimlibProcessRunner.RunAsync(patchCmd, _ => { }, cancellationToken);
                if (patchRes.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to patch rescue boot.wim (exit {patchRes.ExitCode}): {patchRes.Output}");
                }
            }
            finally
            {
                if (File.Exists(tempIni))
                {
                    File.Delete(tempIni);
                }
            }

            progress?.Report("Building USB UEFI BCD store...");
            var bcdPath = Path.Combine(msBootDir, "BCD");
            if (File.Exists(bcdPath))
            {
                ClearReadOnlyAttribute(bcdPath);
                File.Delete(bcdPath);
            }

            var createRes = await SystemTools.RunAsync(bcdedit, ["/createstore", bcdPath]);
            if (createRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"bcdedit /createstore failed: {createRes.Output.Trim()}");
            }

            var osId = "{" + Guid.NewGuid() + "}";
            var deviceId = "{" + Guid.NewGuid() + "}";
            foreach (var args in RescueUsbPlanner.BuildUsbUefiBcdCommands(bcdPath, osId, deviceId))
            {
                var res = await SystemTools.RunAsync(bcdedit, args);
                if (res.ExitCode != 0)
                {
                    throw new InvalidOperationException($"bcdedit {string.Join(' ', args)} failed: {res.Output.Trim()}");
                }
            }

            progress?.Report($"Rescue USB ready on {fat32Letter}:");
        }
        finally
        {
            peMount?.Dispose();
            winMount?.Dispose();
        }
    }

    private static string? ResolveBootMgfw(string? windowsSourcePath, out IsoMountLease? winMount)
    {
        winMount = null;
        var hostBootMgfw = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Boot",
            "EFI",
            "bootmgfw.efi");
        if (File.Exists(hostBootMgfw))
        {
            return hostBootMgfw;
        }

        if (!string.IsNullOrWhiteSpace(windowsSourcePath)
            && Path.GetExtension(windowsSourcePath).Equals(".iso", StringComparison.OrdinalIgnoreCase)
            && File.Exists(windowsSourcePath))
        {
            winMount = IsoMountLease.AttachAsync(windowsSourcePath, CancellationToken.None).GetAwaiter().GetResult();
            var isoBoot = Path.Combine(winMount.RootPath, "efi", "boot", "bootx64.efi");
            if (File.Exists(isoBoot))
            {
                return isoBoot;
            }
        }

        return null;
    }

    private static async Task<(string BootWim, string BootSdi)> ResolvePeFilesAsync(
        FelixPeSource peSource,
        CancellationToken cancellationToken,
        Action<IsoMountLease> setMount)
    {
        if (!peSource.IsIso)
        {
            return (
                peSource.BootWimPath ?? throw new FileNotFoundException("boot.wim missing."),
                peSource.BootSdiPath ?? throw new FileNotFoundException("boot.sdi missing."));
        }

        var mount = await IsoMountLease.AttachAsync(peSource.DisplayPath, cancellationToken);
        setMount(mount);
        var bootWim = new[]
            {
                Path.Combine(mount.RootPath, "sources", "boot.wim"),
                Path.Combine(mount.RootPath, "boot.wim")
            }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("boot.wim was not found inside ISO.");
        var bootSdi = new[]
            {
                Path.Combine(mount.RootPath, "boot", "boot.sdi"),
                Path.Combine(mount.RootPath, "boot.sdi")
            }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("boot.sdi was not found inside ISO.");

        return (bootWim, bootSdi);
    }

    private static char AllocateFreeLetter(StorageSnapshot snapshot)
    {
        var used = snapshot.OccupiedLetters.Select(char.ToUpperInvariant).ToHashSet();
        foreach (var c in new[] { 'U', 'V', 'R', 'Q' }.Concat(Enumerable.Range('D', 23).Select(i => (char)i)))
        {
            var upper = char.ToUpperInvariant(c);
            if (!used.Contains(upper))
            {
                return upper;
            }
        }

        throw new InvalidOperationException("No free drive letter is available for USB formatting.");
    }

    private static async Task WaitForRootAsync(char letter, CancellationToken cancellationToken)
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

        throw new TimeoutException($"Formatted USB partition {root} did not mount in time.");
    }

    private static void ClearReadOnlyAttribute(string filePath)
    {
        var attrs = File.GetAttributes(filePath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
        }
    }

    private static void CopyDirectoryFiltered(string sourceDir, string destinationDir)
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

    private static string RequireTool(string fileName) =>
        SystemTools.Find(fileName)
        ?? throw new FileNotFoundException($"Required system tool was not found: {fileName}", fileName);
}
