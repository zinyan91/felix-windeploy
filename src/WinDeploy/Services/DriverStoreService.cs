using System.IO;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed record DriverExportResult(
    int ExportedCount,
    IReadOnlyList<string> Errors);

public sealed record DriverInjectionResult(
    bool Succeeded,
    IReadOnlyList<string> Errors);

public interface IDriverStoreService
{
    Task<IReadOnlyList<InstalledDriverEntry>> ListThirdPartyDriversAsync(
        CancellationToken cancellationToken = default);

    Task<DriverExportResult> ExportSelectedDriversAsync(
        IReadOnlyList<InstalledDriverEntry> drivers,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<DriverInjectionResult> InjectOfflineDriversAsync(
        string windowsRoot,
        string driversFolder,
        bool recurse,
        CancellationToken cancellationToken = default);

    Task<DriverInjectionResult> LoadWinPeDriversAsync(
        string winPeDriversFolder,
        CancellationToken cancellationToken = default);
}

public sealed class NativeDriverStoreService : IDriverStoreService
{
    public async Task<IReadOnlyList<InstalledDriverEntry>> ListThirdPartyDriversAsync(
        CancellationToken cancellationToken = default)
    {
        var dism = RequireTool("dism.exe");
        var result = await SystemTools.RunAsync(dism, ["/English", "/Online", "/Get-Drivers"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dism /Get-Drivers failed with exit code {result.ExitCode}: {result.Output}");
        }

        return DriverStoreParser.ParseDismGetDrivers(result.Output, excludeInbox: true);
    }

    public async Task<DriverExportResult> ExportSelectedDriversAsync(
        IReadOnlyList<InstalledDriverEntry> drivers,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (drivers.Count == 0)
        {
            return new DriverExportResult(0, []);
        }

        Directory.CreateDirectory(destinationDirectory);
        var pnputil = RequireTool("pnputil.exe");
        var exported = 0;
        var errors = new List<string>();

        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = SanitizeSegment(Path.GetFileNameWithoutExtension(driver.PublishedName));
            var targetDir = Path.Combine(destinationDirectory, folderName);
            Directory.CreateDirectory(targetDir);

            var result = await SystemTools.RunAsync(
                pnputil,
                ["/export-driver", driver.PublishedName, targetDir]);

            if (result.ExitCode == 0)
            {
                exported++;
            }
            else
            {
                errors.Add($"{driver.PublishedName}: pnputil exit {result.ExitCode} - {result.Output.Trim()}");
            }
        }

        return new DriverExportResult(exported, errors);
    }

    public async Task<DriverInjectionResult> InjectOfflineDriversAsync(
        string windowsRoot,
        string driversFolder,
        bool recurse,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(driversFolder) || !Directory.Exists(driversFolder))
        {
            return new DriverInjectionResult(false, [$"Drivers folder does not exist: {driversFolder}"]);
        }

        var dism = RequireTool("dism.exe");
        var imageArg = "/Image:" + Path.TrimEndingDirectorySeparator(windowsRoot);
        var args = new List<string>
        {
            "/English",
            imageArg,
            "/Add-Driver",
            "/Driver:" + driversFolder
        };
        if (recurse)
        {
            args.Add("/Recurse");
        }

        var result = await SystemTools.RunAsync(dism, args);
        return result.ExitCode == 0
            ? new DriverInjectionResult(true, [])
            : new DriverInjectionResult(false, [$"dism /Add-Driver exit {result.ExitCode}: {result.Output.Trim()}"]);
    }

    public async Task<DriverInjectionResult> LoadWinPeDriversAsync(
        string winPeDriversFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(winPeDriversFolder) || !Directory.Exists(winPeDriversFolder))
        {
            return new DriverInjectionResult(false, [$"WinPE drivers folder does not exist: {winPeDriversFolder}"]);
        }

        if (!PeEnvironment.IsWinPe)
        {
            return new DriverInjectionResult(true, ["Skipped drvload outside WinPE."]);
        }

        var drvload = RequireTool("drvload.exe");
        var infFiles = Directory.EnumerateFiles(winPeDriversFolder, "*.inf", SearchOption.AllDirectories).ToList();
        if (infFiles.Count == 0)
        {
            return new DriverInjectionResult(true, []);
        }

        var errors = new List<string>();
        foreach (var inf in infFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SystemTools.RunAsync(drvload, [inf]);
            if (result.ExitCode != 0)
            {
                errors.Add($"{Path.GetFileName(inf)}: drvload exit {result.ExitCode} - {result.Output.Trim()}");
            }
        }

        return new DriverInjectionResult(errors.Count == 0, errors);
    }

    private static string RequireTool(string fileName) =>
        SystemTools.Find(fileName)
        ?? throw new FileNotFoundException($"Required system tool was not found: {fileName}", fileName);

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "driver";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }
}
