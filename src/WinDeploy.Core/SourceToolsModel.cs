using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public enum DownloadToolKind
{
    HasleoIsoDownloader,
    MctWindows11,
    MctWindows10,
    UupDump,
    FidoScript,
    WorCatalog
}

public enum DownloadActionKind
{
    DownloadZipAndRun,
    SaveExeAndRun,
    DownloadScriptAndRun,
    OpenBrowser
}

public sealed record DownloadToolEntry(
    DownloadToolKind Kind,
    string Name,
    DownloadActionKind Action,
    string PrimaryUrl,
    string WebsiteUrl);

public static class SourceDownloadCatalog
{
    public const string OfficialDownloadUrl = "https://www.felixplatforms.tech/windows";

    public static IReadOnlyList<DownloadToolEntry> All { get; } =
    [
        new(
            DownloadToolKind.HasleoIsoDownloader,
            "Hasleo Windows ISO Downloader",
            DownloadActionKind.DownloadZipAndRun,
            "https://www.easyuefi.com/windows-iso-downloader/downloads/WinISODownloader_Portable.zip",
            "https://www.easyuefi.com/windows-iso-downloader/windows-iso-downloader.html"),
        new(
            DownloadToolKind.MctWindows11,
            "Microsoft Media Creation Tool — Windows 11",
            DownloadActionKind.SaveExeAndRun,
            "https://go.microsoft.com/fwlink/?linkid=2156295",
            "https://www.microsoft.com/software-download/windows11"),
        new(
            DownloadToolKind.MctWindows10,
            "Microsoft Media Creation Tool — Windows 10",
            DownloadActionKind.SaveExeAndRun,
            "https://go.microsoft.com/fwlink/?LinkId=2265055",
            "https://www.microsoft.com/software-download/windows10"),
        new(
            DownloadToolKind.UupDump,
            "UUP Dump",
            DownloadActionKind.OpenBrowser,
            "https://uupdump.net/",
            "https://uupdump.net/"),
        new(
            DownloadToolKind.FidoScript,
            "Fido (PowerShell)",
            DownloadActionKind.DownloadScriptAndRun,
            "https://raw.githubusercontent.com/pbatard/Fido/master/Fido.ps1",
            "https://github.com/pbatard/Fido"),
        new(
            DownloadToolKind.WorCatalog,
            "WoR — ESD catalog",
            DownloadActionKind.OpenBrowser,
            "https://worproject.com/esd",
            "https://worproject.com/esd")
    ];
}

public sealed record SourceIntegrityReport(
    string FilePath,
    long FileSizeBytes,
    string Sha256Hex,
    string? ExpectedSha256Hex,
    bool? HashMatchesSidecar,
    bool? SignatureValid,
    string? SignerSubject);

public static partial class SourceIntegrityChecker
{
    [GeneratedRegex(@"\b(?<hash>[A-Fa-f0-9]{64})\b")]
    private static partial Regex Sha256HexRegex();

    public static string? TryReadSidecarSha256(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var sidecarPath = imagePath + ".sha256";
        if (File.Exists(sidecarPath))
        {
            var extracted = ExtractHashForFile(File.ReadAllLines(sidecarPath), Path.GetFileName(imagePath));
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        var dir = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sumsPath = Path.Combine(dir, "SHA256SUMS.txt");
            if (File.Exists(sumsPath))
            {
                return ExtractHashForFile(File.ReadAllLines(sumsPath), Path.GetFileName(imagePath), requireFileName: true);
            }
        }

        return null;
    }

    private static string? ExtractHashForFile(IEnumerable<string> lines, string fileName, bool requireFileName = false)
    {
        string? firstCandidate = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var match = Sha256HexRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var hex = match.Groups["hash"].Value.ToUpperInvariant();
            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hex;
            }

            firstCandidate ??= hex;
        }

        return requireFileName ? null : firstCandidate;
    }

    public static async Task<string> ComputeSha256Async(
        string filePath,
        IProgress<int>? percent = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4 * 1024 * 1024,
            useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[4 * 1024 * 1024];
        long totalRead = 0;
        var totalLength = Math.Max(1L, stream.Length);
        var lastPercent = -1;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            totalRead += read;
            var pct = (int)Math.Min(100, totalRead * 100 / totalLength);
            if (pct != lastPercent)
            {
                lastPercent = pct;
                percent?.Report(pct);
            }
        }

        percent?.Report(100);
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
