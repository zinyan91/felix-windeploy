using System.IO;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed record ToolArtifactResult(
    bool Succeeded,
    string? ArtifactPath,
    string Message);

public interface INetworkAndPrinterBackupService
{
    bool IsPrintBrmAvailable();

    Task<ToolArtifactResult> ExportConnectedWifiProfileAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<ToolArtifactResult> ExportPrintersAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class NativeNetworkAndPrinterBackupService : INetworkAndPrinterBackupService
{
    private static string PrintBrmExecutablePath =>
        Path.Combine(Environment.SystemDirectory, "spool", "tools", "PrintBrm.exe");

    public bool IsPrintBrmAvailable() =>
        !PeEnvironment.IsWinPe && File.Exists(PrintBrmExecutablePath);

    public async Task<ToolArtifactResult> ExportConnectedWifiProfileAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var netsh = SystemTools.Find("netsh.exe");
        if (string.IsNullOrEmpty(netsh))
        {
            return new ToolArtifactResult(false, null, "netsh.exe was not found.");
        }

        var showResult = await SystemTools.RunAsync(netsh, ["wlan", "show", "interfaces"]);
        if (showResult.ExitCode != 0)
        {
            return new ToolArtifactResult(false, null, showResult.Output.Trim());
        }

        var ssid = WifiProfileParser.ParseConnectedSsid(showResult.Output);
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return new ToolArtifactResult(false, null, "No connected Wi-Fi profile detected.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var exportResult = await SystemTools.RunAsync(
            netsh,
            ["wlan", "export", "profile", $"name={ssid}", "key=clear", $"folder={destinationDirectory}"]);

        if (exportResult.ExitCode != 0)
        {
            return new ToolArtifactResult(false, null, exportResult.Output.Trim());
        }

        var exportedXml = Directory.EnumerateFiles(destinationDirectory, "*.xml")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;

        if (exportedXml is null || !WifiProfileParser.IsValidWifiProfileXml(await File.ReadAllTextAsync(exportedXml, cancellationToken)))
        {
            return new ToolArtifactResult(false, null, "Exported Wi-Fi profile XML is missing or invalid.");
        }

        return new ToolArtifactResult(true, exportedXml, $"Exported Wi-Fi profile '{ssid}'.");
    }

    public async Task<ToolArtifactResult> ExportPrintersAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPrintBrmAvailable())
        {
            return new ToolArtifactResult(false, null, "PrintBrm.exe is not available in this environment.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var outputFile = Path.Combine(destinationDirectory, "Printers.printerExport");
        var result = await SystemTools.RunAsync(
            PrintBrmExecutablePath,
            ["-B", "-F", outputFile, "-O", "FORCE"]);

        if (result.ExitCode != 0 || !File.Exists(outputFile))
        {
            return new ToolArtifactResult(false, null, result.Output.Trim());
        }

        return new ToolArtifactResult(true, outputFile, $"Exported printers to {Path.GetFileName(outputFile)}.");
    }
}
