using System.Diagnostics;
using System.IO;
using Felix.Partition.Services.Native;

namespace Felix.WindowSetup.Services;

public sealed record FelixPeSource(
    string DisplayPath,
    bool IsIso,
    string? BootWimPath,
    string? BootSdiPath,
    bool NeedsWinpeshlInit = false);

public static class FelixPeLocator
{
    public static FelixPeSource? TryResolve(string? explicitPath, string? windowsSourcePath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return ValidateCandidate(explicitPath.Trim());
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (Directory.Exists(baseDir))
        {
            candidates.AddRange(Directory.EnumerateFiles(baseDir, "FelixPE*.iso"));
            candidates.Add(Path.Combine(baseDir, "FelixPE"));
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "FelixPE"));
            }
        }

        const string devOut = @"D:\FelixPE\.tmp\out";
        if (Directory.Exists(devOut))
        {
            foreach (var dir in Directory.EnumerateDirectories(devOut, "FelixPE_*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                candidates.AddRange(Directory.EnumerateFiles(dir, "FelixPE_*.iso"));
            }
            candidates.Add(Path.Combine(devOut, "FelixPE.iso"));
        }

        foreach (var candidate in candidates)
        {
            var resolved = ValidateCandidate(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (TryResolveLocalWinRe() is { } winReSource)
        {
            return winReSource;
        }

        return TryResolveWindowsIsoFallback(windowsSourcePath);
    }

    public static FelixPeSource? ValidateCandidate(string path)
    {
        if (File.Exists(path))
        {
            if (string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
            {
                return new FelixPeSource(path, IsIso: true, BootWimPath: null, BootSdiPath: null, NeedsWinpeshlInit: false);
            }

            if (string.Equals(Path.GetExtension(path), ".wim", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                var sdi = FindSdiNear(dir);
                if (sdi is not null)
                {
                    return new FelixPeSource(path, IsIso: false, BootWimPath: path, BootSdiPath: sdi, NeedsWinpeshlInit: false);
                }
            }

            return null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var directWim = Path.Combine(path, "boot.wim");
        var sourcesWim = Path.Combine(path, "sources", "boot.wim");
        var wim = File.Exists(directWim) ? directWim : (File.Exists(sourcesWim) ? sourcesWim : null);
        if (wim is null)
        {
            return null;
        }

        var sdiFile = FindSdiNear(path);
        return sdiFile is null
            ? null
            : new FelixPeSource(path, IsIso: false, BootWimPath: wim, BootSdiPath: sdiFile, NeedsWinpeshlInit: false);
    }

    private static FelixPeSource? TryResolveLocalWinRe()
    {
        var bootSdi = FindHostBootSdi();
        if (bootSdi is null)
        {
            return null;
        }

        var winRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localWinRe = Path.Combine(winRoot, "System32", "Recovery", "Winre.wim");
        if (File.Exists(localWinRe))
        {
            return new FelixPeSource(localWinRe, IsIso: false, BootWimPath: localWinRe, BootSdiPath: bootSdi, NeedsWinpeshlInit: true);
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, "Recovery", "WindowsRE", "Winre.wim");
                if (File.Exists(candidate))
                {
                    return new FelixPeSource(candidate, IsIso: false, BootWimPath: candidate, BootSdiPath: bootSdi, NeedsWinpeshlInit: true);
                }
            }
        }

        var reagentLocation = QueryReagentWinReLocation();
        if (!string.IsNullOrWhiteSpace(reagentLocation))
        {
            var winreFile = Path.Combine(reagentLocation, "Winre.wim");
            if (File.Exists(winreFile))
            {
                return new FelixPeSource(winreFile, IsIso: false, BootWimPath: winreFile, BootSdiPath: bootSdi, NeedsWinpeshlInit: true);
            }
        }

        return null;
    }

    private static FelixPeSource? TryResolveWindowsIsoFallback(string? windowsSourcePath)
    {
        if (string.IsNullOrWhiteSpace(windowsSourcePath))
        {
            return null;
        }

        var trimmed = windowsSourcePath.Trim();
        if (string.Equals(Path.GetExtension(trimmed), ".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            return new FelixPeSource(trimmed, IsIso: true, BootWimPath: null, BootSdiPath: null, NeedsWinpeshlInit: true);
        }

        var dir = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var bootWim = Path.Combine(dir, "boot.wim");
            var bootSdi = FindSdiNear(dir) ?? FindHostBootSdi();
            if (File.Exists(bootWim) && bootSdi is not null)
            {
                return new FelixPeSource(bootWim, IsIso: false, BootWimPath: bootWim, BootSdiPath: bootSdi, NeedsWinpeshlInit: true);
            }
        }

        return null;
    }

    private static string? FindHostBootSdi()
    {
        var winRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(winRoot, "Boot", "DVD", "EFI", "boot.sdi"),
            Path.Combine(winRoot, "Boot", "DVD", "PCAT", "boot.sdi"),
            Path.Combine(winRoot, "System32", "boot.sdi")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? QueryReagentWinReLocation()
    {
        try
        {
            var reagentc = SystemTools.Find("reagentc.exe");
            if (string.IsNullOrEmpty(reagentc))
            {
                return null;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = reagentc,
                Arguments = "/info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var idx = line.IndexOf(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return line[idx..].Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? FindSdiNear(string directory)
    {
        var candidates = new[]
        {
            Path.Combine(directory, "boot.sdi"),
            Path.Combine(directory, "boot", "boot.sdi"),
            Path.Combine(Directory.GetParent(directory)?.FullName ?? directory, "boot", "boot.sdi"),
            Path.Combine(Directory.GetParent(directory)?.FullName ?? directory, "boot.sdi")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
