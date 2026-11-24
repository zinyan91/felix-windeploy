using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Felix.Partition.Services.Native;

/// <summary>
/// Runs diskpart.exe with a generated script. diskpart ships with every Windows and WinPE image and moves files
/// when shrinking, which the raw volume APIs do not. Scripts are built only from numbers and fixed keywords.
/// </summary>
public static partial class DiskPart
{
    public static string SelectPartition(int disk, int partition) =>
        $"select disk {disk.ToString(CultureInfo.InvariantCulture)}\nselect partition {partition.ToString(CultureInfo.InvariantCulture)}\n";

    public static string FileSystemKeyword(string fileSystem) => fileSystem switch
    {
        "NTFS" => "ntfs", "FAT32" => "fat32", "exFAT" => "exfat",
        _ => throw new InvalidOperationException("Partition_Error_FileSystem")
    };

    private const long MiB = 1048576;
    private static long AlignDown(long n) => n / MiB * MiB;

    public static long Mebibytes(long bytes) => bytes / MiB;

    /// <summary>
    /// diskpart prints the reclaimable space as "318 GB (325767 MB)", or as a bare "127 MB" when small; the units
    /// are the same in every language it ships. Rounded down to MiB so the shrink limit is never overstated.
    /// </summary>
    public static long ParseQueryMax(string output)
    {
        var exact = QueryMaxPattern().Matches(output);
        if (exact.Count > 0) return long.Parse(exact[^1].Groups[1].Value, CultureInfo.InvariantCulture) * MiB;
        var plain = SizePattern().Matches(output);
        if (plain.Count == 0) throw new InvalidOperationException("Partition_Error_ReadTimeout");
        var value = long.Parse(plain[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var bytes = plain[^1].Groups[2].Value switch
        {
            "B" => value, "KB" => value * 1024, "MB" => value * MiB,
            "GB" => value * 1024 * MiB, _ => value * 1024L * 1024 * MiB
        };
        return AlignDown(bytes);
    }

    /// <summary>"detail partition" lists the offset in bytes; the exact decimal proves diskpart selected the reviewed partition.</summary>
    public static bool ShowsOffset(string output, long offset) =>
        Regex.IsMatch(output, @"(?<![\d.,])" + offset.ToString(CultureInfo.InvariantCulture) + @"(?![\d.,])");

    public static async Task<string> RunAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(Path.GetTempPath(), "felix-partition-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, script + "exit\n", Encoding.ASCII, CancellationToken.None);
        try
        {
            var oem = OemEncoding();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "diskpart.exe"),
                    Arguments = "/s \"" + file + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = oem, StandardErrorEncoding = oem
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            if (timeout is TimeSpan limit)
            {
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                guard.CancelAfter(limit);
                try { await process.WaitForExitAsync(guard.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync(CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Partition_Error_ReadTimeout");
                }
            }
            // Writes are never killed midway: an interrupted diskpart is worse than a slow one.
            else await process.WaitForExitAsync(CancellationToken.None);
            var output = await stdout + await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(LastMessage(output));
            return output;
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    public static string LastMessage(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("DISKPART>", StringComparison.Ordinal)).ToArray();
        return lines.Length == 0 ? "diskpart" : string.Join("\n", lines.TakeLast(2));
    }

    private static Encoding OemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (ArgumentException) { return Encoding.ASCII; }
    }

    [GeneratedRegex(@"\((\d+)\s*MB\)")]
    private static partial Regex QueryMaxPattern();

    [GeneratedRegex(@"(?<![\d.,])(\d+)\s*(B|KB|MB|GB|TB)\b")]
    private static partial Regex SizePattern();
}
