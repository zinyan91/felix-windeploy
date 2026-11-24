using System.IO;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface IExtraBootEntriesService
{
    IReadOnlyList<ParallelWindowsEntry> DiscoverParallelWindows(
        char excludeWindowsLetter,
        FirmwareMode firmware);

    Task<IReadOnlyList<string>> AddParallelWindowsEntriesAsync(
        char systemLetter,
        char excludeWindowsLetter,
        FirmwareMode firmware,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> AddExtraWinPeEntryAsync(
        char systemLetter,
        char windowsLetter,
        FirmwareMode firmware,
        string wimPath,
        string title,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> FindWinPeWimsAsync(
        IReadOnlyList<TargetDiskFacts> disks,
        CancellationToken cancellationToken = default);
}

public sealed class NativeExtraBootEntriesService : IExtraBootEntriesService
{
    private static readonly string[] IgnoredDirectories =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Users"
    ];

    public IReadOnlyList<ParallelWindowsEntry> DiscoverParallelWindows(
        char excludeWindowsLetter,
        FirmwareMode firmware)
    {
        var entries = new List<ParallelWindowsEntry>();
        var excluded = char.ToUpperInvariant(excludeWindowsLetter);
        var loaderName = firmware == FirmwareMode.UefiGpt ? "winload.efi" : "winload.exe";

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var letter = char.ToUpperInvariant(drive.Name[0]);
            if (letter == 'X' || letter == excluded)
            {
                continue;
            }

            var windowsDir = Path.Combine(drive.RootDirectory.FullName, "Windows");
            var kernel = Path.Combine(windowsDir, "System32", "ntoskrnl.exe");
            var loader = Path.Combine(windowsDir, "System32", loaderName);
            if (!File.Exists(kernel) || !File.Exists(loader))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? $"Windows ({letter}:)"
                : $"{drive.VolumeLabel} ({letter}:)";
            entries.Add(new ParallelWindowsEntry(letter, windowsDir, label));
        }

        return entries;
    }

    public async Task<IReadOnlyList<string>> AddParallelWindowsEntriesAsync(
        char systemLetter,
        char excludeWindowsLetter,
        FirmwareMode firmware,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var entries = DiscoverParallelWindows(excludeWindowsLetter, firmware);
        if (entries.Count == 0)
        {
            log.Add("Parallel Windows: no other Windows installations found.");
            return log;
        }

        var bcdboot = RequireTool("bcdboot.exe");
        var bcdedit = RequireTool("bcdedit.exe");
        var added = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = ParallelWindowsPlanner.BuildBcdBootArguments(entry, systemLetter, firmware);
            var result = await SystemTools.RunAsync(bcdboot, args);
            if (result.ExitCode == 0)
            {
                added++;
                log.Add($"Added parallel Windows entry from {entry.WindowsDirectory}");
            }
            else
            {
                log.Add($"Failed adding parallel Windows {entry.WindowsDirectory}: exit {result.ExitCode} - {result.Output.Trim()}");
            }
        }

        if (added > 0)
        {
            var store = GetBcdStorePath(systemLetter, firmware);
            var timeout = await SystemTools.RunAsync(bcdedit, ["/store", store, "/timeout", "10"]);
            log.Add($"Set boot timeout=10s: exit {timeout.ExitCode}");
        }

        return log;
    }

    public async Task<IReadOnlyList<string>> AddExtraWinPeEntryAsync(
        char systemLetter,
        char windowsLetter,
        FirmwareMode firmware,
        string wimPath,
        string title,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(wimPath))
        {
            log.Add($"Extra WinPE missing: {wimPath}");
            return log;
        }

        EnsureBootSdi(systemLetter, windowsLetter, log);

        var store = GetBcdStorePath(systemLetter, firmware);
        var bcdedit = RequireTool("bcdedit.exe");
        var ramdiskCreate = await SystemTools.RunAsync(bcdedit, ["/store", store, "/create", "{ramdiskoptions}"]);
        log.Add($"Ensure {{ramdiskoptions}}: exit {ramdiskCreate.ExitCode}");

        var entryTitle = string.IsNullOrWhiteSpace(title) ? "Felix WinPE" : title.Trim();
        var osloaderCreate = await SystemTools.RunAsync(
            bcdedit,
            ["/store", store, "/create", "/d", entryTitle, "/application", "osloader"]);
        if (osloaderCreate.ExitCode != 0)
        {
            log.Add($"Failed creating Extra WinPE BCD entry: {osloaderCreate.Output.Trim()}");
            return log;
        }

        var guid = ExtraWinPePlanner.ParseCreatedBcdGuid(osloaderCreate.Output);
        if (string.IsNullOrWhiteSpace(guid))
        {
            log.Add($"Could not parse GUID from bcdedit output: {osloaderCreate.Output.Trim()}");
            return log;
        }

        var (driveLetter, relativePath) = ExtraWinPePlanner.ResolveRelativePath(wimPath);
        var commands = ExtraWinPePlanner.BuildRamdiskEntryCommands(
            store,
            guid,
            driveLetter,
            relativePath,
            firmware,
            setTimeoutTenSeconds: true);

        foreach (var cmdArgs in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var res = await SystemTools.RunAsync(bcdedit, cmdArgs);
            if (res.ExitCode != 0)
            {
                log.Add($"bcdedit {string.Join(' ', cmdArgs)} => exit {res.ExitCode}: {res.Output.Trim()}");
            }
        }

        log.Add($"Added Extra WinPE entry '{entryTitle}' ({guid}) for {wimPath}");
        return log;
    }

    public async Task<IReadOnlyList<string>> FindWinPeWimsAsync(
        IReadOnlyList<TargetDiskFacts> disks,
        CancellationToken cancellationToken = default)
    {
        var dism = SystemTools.Find("dism.exe");
        if (string.IsNullOrEmpty(dism))
        {
            return [];
        }

        var candidates = new List<string>();
        foreach (var disk in disks)
        {
            if (disk.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in disk.Partitions)
            {
                if (part.IsBitLocker || string.IsNullOrWhiteSpace(part.Letter))
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(part.Letter.TrimEnd(':')[0]);
                if (letter == 'X')
                {
                    continue;
                }

                var root = $@"{letter}:\";
                if (Directory.Exists(root))
                {
                    CollectWimsBounded(root, maxDepth: 2, depth: 0, candidates);
                }
            }
        }

        var found = new List<string>();
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await SystemTools.RunAsync(
                dism,
                ["/English", "/Get-WimInfo", $"/WimFile:{path}", "/Index:1"]);
            if (info.ExitCode == 0 && WinPeImageClassifier.LooksLikeWinPe(info.Output, path))
            {
                found.Add(path);
            }
        }

        return found;
    }

    private static void CollectWimsBounded(string directory, int maxDepth, int depth, ICollection<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.wim"))
            {
                results.Add(file);
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var subDir in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(subDir);
                if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectWimsBounded(subDir, maxDepth, depth + 1, results);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void EnsureBootSdi(char systemLetter, char windowsLetter, ICollection<string> log)
    {
        var destDir = $@"{char.ToUpperInvariant(systemLetter)}:\Boot";
        var dest = Path.Combine(destDir, "boot.sdi");
        if (File.Exists(dest))
        {
            return;
        }

        var win = char.ToUpperInvariant(windowsLetter);
        var sources = new[]
        {
            $@"{win}:\Windows\Boot\DVD\EFI\boot.sdi",
            $@"{win}:\Windows\Boot\DVD\PCAT\boot.sdi",
            @"X:\Windows\Boot\DVD\EFI\boot.sdi",
            @"X:\Windows\Boot\DVD\PCAT\boot.sdi"
        };

        var source = sources.FirstOrDefault(File.Exists);
        if (source is null)
        {
            log.Add("Warning: boot.sdi was not found on target Windows or WinPE.");
            return;
        }

        Directory.CreateDirectory(destDir);
        File.Copy(source, dest, overwrite: true);
        log.Add($"Copied boot.sdi from {source} to {dest}");
    }

    private static string GetBcdStorePath(char systemLetter, FirmwareMode firmware)
    {
        var root = $@"{char.ToUpperInvariant(systemLetter)}:\";
        return firmware == FirmwareMode.UefiGpt
            ? Path.Combine(root, "EFI", "Microsoft", "Boot", "BCD")
            : Path.Combine(root, "Boot", "BCD");
    }

    private static string RequireTool(string fileName) =>
        SystemTools.Find(fileName)
        ?? throw new FileNotFoundException($"Required system tool was not found: {fileName}", fileName);
}
