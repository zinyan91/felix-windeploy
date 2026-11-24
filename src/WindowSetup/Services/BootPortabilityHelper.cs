using System.IO;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;
using Microsoft.Win32;

namespace Felix.WindowSetup.Services;

public static class BootPortabilityHelper
{
    private static readonly string[] CriticalStorageServices =
    [
        "storahci",
        "stornvme",
        "iaStorV",
        "iaStorAVC",
        "iaStorAC",
        "amd_sata",
        "amd_xata",
        "LSI_SAS",
        "LSI_SAS2",
        "LSI_SAS3",
        "megasas",
        "megasas2",
        "megasas35",
        "nvraid",
        "vmbus",
        "storvsc",
        "pciide",
        "atapi",
        "intelide"
    ];

    public static Task<IReadOnlyList<string>> ApplyToOfflineWindowsAsync(
        string windowsRoot,
        CancellationToken cancellationToken = default)
    {
        var systemHive = Path.Combine(windowsRoot, "Windows", "System32", "config", "SYSTEM");
        return ApplyToSystemHiveFileAsync(systemHive, cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> ApplyToSystemHiveFileAsync(
        string systemHivePath,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        if (!File.Exists(systemHivePath))
        {
            logs.Add($"SYSTEM hive not found at {systemHivePath}; skipped portability patch.");
            return logs;
        }

        var regExe = SystemTools.Find("reg.exe");
        if (string.IsNullOrEmpty(regExe))
        {
            logs.Add("reg.exe not found; skipped portability patch.");
            return logs;
        }

        var hiveKeyName = $"FelixPortSys_{Guid.NewGuid():N}";
        var fullHivePath = $@"HKLM\{hiveKeyName}";
        var load = await SystemTools.RunAsync(regExe, ["load", fullHivePath, systemHivePath]);
        if (load.ExitCode != 0)
        {
            logs.Add($"Failed to load SYSTEM hive (exit {load.ExitCode}): {load.Output.Trim()}");
            return logs;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PatchLoadedHive(hiveKeyName, logs);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await SystemTools.RunAsync(regExe, ["unload", fullHivePath]);
        }

        return logs;
    }

    public static async Task<IReadOnlyList<string>> PatchCapturedWimPortabilityAsync(
        string wimlibPath,
        string capturedWimPath,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"felix-wim-port-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var extractCmd = new WimlibCommand(
                wimlibPath,
                ["extract", capturedWimPath, "1", @"\Windows\System32\config\SYSTEM", $"--dest-dir={tempDir}", "--no-acls"]);
            var extractRes = await WimlibProcessRunner.RunAsync(extractCmd, _ => { }, cancellationToken);
            var extractedHive = Path.Combine(tempDir, "SYSTEM");
            if (extractRes.ExitCode != 0 || !File.Exists(extractedHive))
            {
                logs.Add($"Could not extract SYSTEM hive for portability patch (exit {extractRes.ExitCode}).");
                return logs;
            }

            var hiveLogs = await ApplyToSystemHiveFileAsync(extractedHive, cancellationToken);
            logs.AddRange(hiveLogs);

            var commandFile = Path.Combine(tempDir, "update-cmds.txt");
            var quotedTemp = extractedHive.Replace("\"", "\\\"");
            await File.WriteAllTextAsync(
                commandFile,
                $"add \"{quotedTemp}\" \\Windows\\System32\\config\\SYSTEM\r\n",
                cancellationToken);

            var res = await RunWimlibUpdateWithScriptAsync(wimlibPath, capturedWimPath, commandFile, cancellationToken);
            logs.Add(res.ExitCode == 0
                ? "Updated captured WIM SYSTEM hive with hardware portability fixes."
                : $"wimlib update exit {res.ExitCode}: {res.Output.Trim()}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }

        return logs;
    }

    private static async Task<(int ExitCode, string Output)> RunWimlibUpdateWithScriptAsync(
        string wimlibPath,
        string wimPath,
        string commandFilePath,
        CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = wimlibPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("update");
        process.StartInfo.ArgumentList.Add(wimPath);
        process.StartInfo.ArgumentList.Add("1");

        process.Start();
        var scriptContent = await File.ReadAllTextAsync(commandFilePath, cancellationToken);
        await process.StandardInput.WriteAsync(scriptContent);
        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var combined = string.Concat(await stdOutTask, "\n", await stdErrTask);
        return (process.ExitCode, combined);
    }

    private static void PatchLoadedHive(string hiveKeyName, List<string> logs)
    {
        using var root = Registry.LocalMachine.OpenSubKey(hiveKeyName, writable: true);
        if (root is null)
        {
            logs.Add("Loaded SYSTEM hive key could not be opened for write.");
            return;
        }

        using (var mounted = root.OpenSubKey("MountedDevices", writable: true))
        {
            if (mounted is not null)
            {
                var names = mounted.GetValueNames();
                foreach (var name in names)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        mounted.DeleteValue(name, throwOnMissingValue: false);
                    }
                }
                logs.Add($"Cleared {names.Length} MountedDevices entries for hardware portability.");
            }
        }

        var currentSet = 1;
        using (var select = root.OpenSubKey("Select", writable: false))
        {
            if (select?.GetValue("Current") is int val && val > 0)
            {
                currentSet = val;
            }
        }

        var controlSetName = $"ControlSet{currentSet:D3}";
        using var services = root.OpenSubKey($@"{controlSetName}\Services", writable: true);
        if (services is null)
        {
            return;
        }

        var adjusted = 0;
        foreach (var serviceName in CriticalStorageServices)
        {
            using var svc = services.OpenSubKey(serviceName, writable: true);
            if (svc is null)
            {
                continue;
            }

            if (svc.GetValue("Start") is int startVal && startVal != 0)
            {
                svc.SetValue("Start", 0, RegistryValueKind.DWord);
                adjusted++;
            }
        }

        logs.Add($"Forced Start=0 on {adjusted} boot-critical storage services in {controlSetName}.");
    }
}
