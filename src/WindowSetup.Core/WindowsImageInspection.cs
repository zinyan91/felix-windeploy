using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public sealed record WindowsImageEdition(
    int Index,
    string Name,
    string? Architecture,
    string? Version,
    int? ServicePackBuild = null,
    long? TotalBytes = null,
    bool IsWindowsPe = false)
{
    public string DisplayName
    {
        get
        {
            var build = Version?.Split('.').LastOrDefault();
            var version = int.TryParse(build, out var parsedBuild)
                ? $"{SourceImages.ReleaseName(parsedBuild) ?? parsedBuild.ToString()} ({parsedBuild}{(ServicePackBuild is int sp ? $".{sp}" : string.Empty)})"
                : Version;
            var name = string.IsNullOrWhiteSpace(Name) ? "Windows image" : Name;
            var sizeText = TotalBytes is > 0
                ? $"{TotalBytes.Value / (1024d * 1024 * 1024):0.0} GB"
                : null;
            return string.Join(" · ", new[] { Index.ToString(), name, Architecture, version, sizeText }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
}

public sealed record WindowsImageInspection(
    string ImagePath,
    IReadOnlyList<WindowsImageEdition> Editions);

public static partial class DismWimInfoParser
{
    public static WindowsImageInspection Parse(string imagePath, string output)
    {
        var editions = new List<WindowsImageEdition>();
        int? index = null;
        string? name = null;
        string? description = null;
        string? architecture = null;
        string? version = null;
        int? servicePackBuild = null;
        long? totalBytes = null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PropertyLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();
            if (key.Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                AddEdition();
                index = int.TryParse(value, out var parsedIndex) ? parsedIndex : null;
                name = null;
                description = null;
                architecture = null;
                version = null;
                servicePackBuild = null;
                totalBytes = null;
            }
            else if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
            else if (key.Equals("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                architecture = value;
            }
            else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                version = value;
            }
            else if (key.Equals("ServicePack Build", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out var parsedServicePackBuild))
            {
                servicePackBuild = parsedServicePackBuild;
            }
            else if (key.Equals("Total Bytes", StringComparison.OrdinalIgnoreCase))
            {
                totalBytes = ParseByteCount(value);
            }
        }

        AddEdition();
        return new WindowsImageInspection(imagePath, editions);

        void AddEdition()
        {
            if (index is not null)
            {
                editions.Add(new WindowsImageEdition(
                    index.Value,
                    name ?? string.Empty,
                    architecture,
                    version,
                    servicePackBuild,
                    totalBytes,
                    WimlibInfoParser.IsWindowsPeMetadata(name, description)));
            }
        }
    }

    internal static long? ParseByteCount(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var parsed) && parsed > 0 ? parsed : null;
    }

    [GeneratedRegex(@"^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)\s*$")]
    private static partial Regex PropertyLine();
}

public static partial class DismWimInfoParser
{
    public static (string? Architecture, string? Version, int? ServicePackBuild) ParseIndex(string output)
    {
        var (architecture, version, servicePackBuild, _) = ParseIndexDetails(output);
        return (architecture, version, servicePackBuild);
    }

    public static (string? Architecture, string? Version, int? ServicePackBuild, long? TotalBytes) ParseIndexDetails(string output)
    {
        string? architecture = null;
        string? version = null;
        int? servicePackBuild = null;
        long? totalBytes = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PropertyLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();
            if (key.Equals("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                architecture = value;
            }
            else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                version = value;
            }
            else if (key.Equals("ServicePack Build", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out var parsed))
            {
                servicePackBuild = parsed;
            }
            else if (key.Equals("Total Bytes", StringComparison.OrdinalIgnoreCase))
            {
                totalBytes = ParseByteCount(value);
            }
        }

        return (architecture, version, servicePackBuild, totalBytes);
    }
}

public static partial class WimlibInfoParser
{
    public static bool IsWindowsPeMetadata(
        string? name,
        string? description = null,
        string? typeOrEdition = null,
        string? flags = null) =>
        string.Equals(flags?.Trim(), "9", StringComparison.OrdinalIgnoreCase)
        || ContainsPeMarker(typeOrEdition)
        || ContainsPeMarker(name)
        || ContainsPeMarker(description);

    private static bool ContainsPeMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("Windows PE", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WindowsPE", StringComparison.OrdinalIgnoreCase)
            || value.Contains("WinPE", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FelixPE", StringComparison.OrdinalIgnoreCase));

    public static WindowsImageInspection Parse(string imagePath, string output)
    {
        var editions = new List<WindowsImageEdition>();
        int? index = null;
        string? name = null;
        string? description = null;
        string? architecture = null;
        string? major = null;
        string? minor = null;
        string? build = null;
        string? flags = null;
        int? spBuild = null;
        long? totalBytes = null;
        bool isWindowsPe = false;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PropertyLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim();

            if (key.Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                AddEdition();
                index = int.TryParse(value, out var parsed) ? parsed : null;
                name = null;
                description = null;
                architecture = null;
                major = null;
                minor = null;
                build = null;
                flags = null;
                spBuild = null;
                totalBytes = null;
                isWindowsPe = false;
            }
            else if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
            else if (key.Equals("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                architecture = NormalizeArchitecture(value);
            }
            else if (key.Equals("Major Version", StringComparison.OrdinalIgnoreCase))
            {
                major = value;
            }
            else if (key.Equals("Minor Version", StringComparison.OrdinalIgnoreCase))
            {
                minor = value;
            }
            else if (key.Equals("Build", StringComparison.OrdinalIgnoreCase))
            {
                build = value;
            }
            else if (key.Equals("Flags", StringComparison.OrdinalIgnoreCase))
            {
                flags = value;
            }
            else if (key.Equals("Service Pack Build", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out var parsedSp))
            {
                spBuild = parsedSp;
            }
            else if (key.Equals("Total Bytes", StringComparison.OrdinalIgnoreCase))
            {
                totalBytes = DismWimInfoParser.ParseByteCount(value);
            }
            else if ((key.Equals("Installation Type", StringComparison.OrdinalIgnoreCase) ||
                      key.Equals("Edition ID", StringComparison.OrdinalIgnoreCase)) &&
                     ContainsPeMarker(value))
            {
                isWindowsPe = true;
            }
        }

        AddEdition();
        return new WindowsImageInspection(imagePath, editions);

        void AddEdition()
        {
            if (index is null)
            {
                return;
            }

            var version = !string.IsNullOrWhiteSpace(build)
                ? string.Join('.', new[] { string.IsNullOrWhiteSpace(major) ? "10" : major, string.IsNullOrWhiteSpace(minor) ? "0" : minor, build })
                : null;
            var peDetected = isWindowsPe || IsWindowsPeMetadata(name, description, null, flags);
            editions.Add(new WindowsImageEdition(index.Value, name ?? string.Empty, architecture, version, spBuild, totalBytes, peDetected));
        }
    }

    private static string NormalizeArchitecture(string value)
    {
        if (value.Equals("x86_64", StringComparison.OrdinalIgnoreCase) || value.Equals("amd64", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }
        if (value.Equals("ARM64", StringComparison.OrdinalIgnoreCase) || value.Equals("aarch64", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64";
        }
        if (value.Equals("x86", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }
        return value;
    }

    [GeneratedRegex(@"^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)\s*$")]
    private static partial Regex PropertyLine();
}

public static class WimlibWindowsImageInspector
{
    public static async Task<WindowsImageInspection> InspectAsync(
        string wimlibExecutablePath,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wimlibExecutablePath) || !File.Exists(wimlibExecutablePath))
        {
            throw new FileNotFoundException("wimlib-imagex.exe could not be found.", wimlibExecutablePath);
        }

        var extension = Path.GetExtension(imagePath);
        if (!string.Equals(extension, ".wim", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".esd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".swm", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Edition inspection currently supports WIM, ESD, and SWM images.");
        }

        if (string.Equals(extension, ".swm", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(imagePath);
            var parts = SourceImages.ResolveSwm(
                imagePath,
                directory is null ? [imagePath] : Directory.EnumerateFiles(directory, "*.swm"));
            if (!parts.IsComplete)
            {
                throw new InvalidOperationException($"The split Windows image is missing part(s): {string.Join(", ", parts.MissingParts)}.");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wimlibExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add(imagePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("wimlib-imagex could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        var inspection = WimlibInfoParser.Parse(imagePath, output);
        if (inspection.Editions.Count == 0)
        {
            throw new InvalidOperationException("wimlib returned no Windows editions.");
        }

        return inspection;
    }
}

public static class DismWindowsImageInspector
{
    public static async Task<WindowsImageInspection> InspectAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(imagePath);
        if (!string.Equals(extension, ".wim", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".esd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".swm", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Edition inspection currently supports WIM, ESD, and SWM images.");
        }
        if (string.Equals(extension, ".swm", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(imagePath);
            var parts = SourceImages.ResolveSwm(
                imagePath,
                directory is null ? [imagePath] : Directory.EnumerateFiles(directory, "*.swm"));
            if (!parts.IsComplete)
            {
                throw new InvalidOperationException($"The split Windows image is missing part(s): {string.Join(", ", parts.MissingParts)}.");
            }
        }

        var (exitCode, output, error) = await RunDismAsync(
            ["/English", "/Get-WimInfo", $"/WimFile:{imagePath}"],
            cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        var inspection = DismWimInfoParser.Parse(imagePath, output);
        if (inspection.Editions.Count == 0)
        {
            throw new InvalidOperationException("DISM returned no Windows editions.");
        }

        var detailed = new List<WindowsImageEdition>(inspection.Editions.Count);
        foreach (var edition in inspection.Editions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idxExit, idxOut, _) = await RunDismAsync(
                ["/English", "/Get-WimInfo", $"/WimFile:{imagePath}", $"/Index:{edition.Index}"],
                cancellationToken);
            if (idxExit == 0)
            {
                var (arch, version, sp, totalBytes) = DismWimInfoParser.ParseIndexDetails(idxOut);
                detailed.Add(edition with
                {
                    Architecture = string.IsNullOrWhiteSpace(arch) ? edition.Architecture : arch,
                    Version = string.IsNullOrWhiteSpace(version) ? edition.Version : version,
                    ServicePackBuild = sp ?? edition.ServicePackBuild,
                    TotalBytes = totalBytes ?? edition.TotalBytes
                });
            }
            else
            {
                detailed.Add(edition);
            }
        }

        return new WindowsImageInspection(imagePath, detailed);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDismAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dism.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("DISM could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }
}

public static class WindowsImageEditions
{
    public static WindowsImageEdition? PickDefault(IReadOnlyList<WindowsImageEdition> editions)
    {
        if (editions.Count == 0)
        {
            return null;
        }

        return editions.FirstOrDefault(e =>
                   e.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase) &&
                   !e.Name.Contains("Education", StringComparison.OrdinalIgnoreCase) &&
                   !e.Name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase))
               ?? editions.FirstOrDefault(e => e.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase))
               ?? editions[0];
    }
}
