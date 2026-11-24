using System.IO;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface IBloatwareService
{
    Task<IReadOnlyList<string>> ScanImageAppsAsync(
        string imagePath,
        int imageIndex,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ApplyOfflineRemovalAsync(
        string windowsRoot,
        BloatwareOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class NativeBloatwareService : IBloatwareService
{
    public async Task<IReadOnlyList<string>> ScanImageAppsAsync(
        string imagePath,
        int imageIndex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || imageIndex <= 0)
        {
            return [];
        }

        IsoMountLease? mount = null;
        try
        {
            var targetWim = imagePath;
            if (Path.GetExtension(imagePath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                mount = await IsoMountLease.AttachAsync(imagePath, cancellationToken);
                targetWim = new[]
                    {
                        Path.Combine(mount.RootPath, "sources", "install.wim"),
                        Path.Combine(mount.RootPath, "sources", "install.esd"),
                        Path.Combine(mount.RootPath, "sources", "install.swm")
                    }
                    .FirstOrDefault(File.Exists)
                    ?? "";
                if (string.IsNullOrEmpty(targetWim))
                {
                    return [];
                }
            }

            var wimlib = WindowSetupEngineLocator.Find();
            var cmd = new WimlibCommand(
                wimlib,
                ["dir", targetWim, imageIndex.ToString(), @"--path=\Program Files\WindowsApps"]);
            var res = await WimlibProcessRunner.RunAsync(cmd, _ => { }, cancellationToken);
            if (res.ExitCode != 0)
            {
                return [];
            }

            return BloatwareCatalog.ParseWimWindowsAppsPaths(res.Output);
        }
        catch
        {
            return [];
        }
        finally
        {
            mount?.Dispose();
        }
    }

    public async Task<IReadOnlyList<string>> ApplyOfflineRemovalAsync(
        string windowsRoot,
        BloatwareOptions options,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        if (!options.Enabled)
        {
            return logs;
        }

        var normalizedRoot = windowsRoot.TrimEnd('\\');
        if (options.RemovePackagePrefixes is { Count: > 0 } prefixes)
        {
            var dism = SystemTools.Find("dism.exe");
            if (string.IsNullOrEmpty(dism))
            {
                logs.Add("Bloatware removal skipped: dism.exe not found.");
            }
            else
            {
                var listResult = await SystemTools.RunAsync(
                    dism,
                    ["/English", $"/Image:{normalizedRoot}", "/Get-ProvisionedAppxPackages"]);
                if (listResult.ExitCode != 0)
                {
                    logs.Add($"Bloatware DISM query failed (exit {listResult.ExitCode}): {listResult.Output.Trim()}");
                }
                else
                {
                    var targets = BloatwareCatalog.ParseDismProvisionedPackages(listResult.Output, prefixes);
                    logs.Add($"Bloatware matched {targets.Count} provisioned package(s).");
                    foreach (var pkg in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var removeResult = await SystemTools.RunAsync(
                            dism,
                            ["/English", $"/Image:{normalizedRoot}", "/Remove-ProvisionedAppxPackage", $"/PackageName:{pkg}"]);
                        logs.Add(removeResult.ExitCode == 0
                            ? $"Removed Appx: {pkg}"
                            : $"Failed to remove Appx {pkg} (exit {removeResult.ExitCode}): {removeResult.Output.Trim()}");
                    }
                }
            }
        }

        if (options.BlockPromotedApps)
        {
            ApplyPromotedAppBlocks(normalizedRoot, logs);
        }

        return logs;
    }

    private static void ApplyPromotedAppBlocks(string windowsRoot, ICollection<string> logs)
    {
        var regExe = SystemTools.Find("reg.exe");
        if (string.IsNullOrEmpty(regExe))
        {
            logs.Add("Block promoted apps skipped: reg.exe not found.");
            return;
        }

        var softwareHive = Path.Combine(windowsRoot, "Windows", "System32", "config", "SOFTWARE");
        var defaultUserHive = Path.Combine(windowsRoot, "Users", "Default", "NTUSER.DAT");

        if (File.Exists(softwareHive))
        {
            ApplyOfflineHiveEdits(
                regExe,
                @"HKLM\FLX_BLOAT_SOFT",
                softwareHive,
                [
                    [@"HKLM\FLX_BLOAT_SOFT\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "1"],
                    [@"HKLM\FLX_BLOAT_SOFT\Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding", "1"]
                ],
                logs);
        }

        if (File.Exists(defaultUserHive))
        {
            ApplyOfflineHiveEdits(
                regExe,
                @"HKLM\FLX_BLOAT_DEFUSER",
                defaultUserHive,
                [
                    [@"HKLM\FLX_BLOAT_DEFUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", "0"],
                    [@"HKLM\FLX_BLOAT_DEFUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "OemPreInstalledAppsEnabled", "0"],
                    [@"HKLM\FLX_BLOAT_DEFUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "PreInstalledAppsEnabled", "0"],
                    [@"HKLM\FLX_BLOAT_DEFUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", "0"],
                    [@"HKLM\FLX_BLOAT_DEFUSER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", "0"]
                ],
                logs);
        }
    }

    private static void ApplyOfflineHiveEdits(
        string regExe,
        string mountKey,
        string hivePath,
        IReadOnlyList<string[]> regDwordValues,
        ICollection<string> logs)
    {
        var load = SystemTools.RunAsync(regExe, ["load", mountKey, hivePath]).GetAwaiter().GetResult();
        if (load.ExitCode != 0)
        {
            logs.Add($"reg load {hivePath} failed (exit {load.ExitCode}): {load.Output.Trim()}");
            return;
        }

        try
        {
            foreach (var entry in regDwordValues)
            {
                var add = SystemTools.RunAsync(regExe, ["add", entry[0], "/v", entry[1], "/t", "REG_DWORD", "/d", entry[2], "/f"]).GetAwaiter().GetResult();
                if (add.ExitCode != 0)
                {
                    logs.Add($"reg add {entry[0]}\\{entry[1]} failed (exit {add.ExitCode})");
                }
            }

            logs.Add($"Applied promoted app policy entries to {Path.GetFileName(hivePath)}.");
        }
        finally
        {
            SystemTools.RunAsync(regExe, ["unload", mountKey]).GetAwaiter().GetResult();
        }
    }
}
