using System.Diagnostics;
using System.Globalization;

namespace Felix.Partition.Services.Native;

public sealed record ToolResult(int ExitCode, string Output);

/// <summary>Microsoft's boot tools from System32 of Windows and WinPE; Felix carries no boot code of its own.</summary>
public static class SystemTools
{
    public const string Mbr2Gpt = "MBR2GPT.EXE";
    public const string Bootsect = "bootsect.exe";
    public const string BcdBoot = "bcdboot.exe";
    public const string BcdEdit = "bcdedit.exe";

    public static async Task<ToolResult> RunAsync(string path, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Start(info);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        return new(process.ExitCode, await stdout + await stderr);
    }

    public static string? Find(string file, string? systemDirectory = null)
    {
        var path = Path.Combine(systemDirectory ?? Environment.SystemDirectory, file);
        return File.Exists(path) ? path : null;
    }

    /// <param name="logs">A folder Felix created; MBR2GPT needs it to exist and writes setupact.log there.</param>
    public static string Mbr2GptArguments(bool convert, int disk, string logs) =>
        $"/{(convert ? "convert" : "validate")} /disk:{disk.ToString(CultureInfo.InvariantCulture)} /logs:\"{logs.TrimEnd('\\')}\"";

    public static string BootsectArguments(char letter) => $"/nt60 {letter}: /mbr";

    public static string Mbr2GptError(int code) => code is >= 1 and <= 10 or 100
        ? "Partition_Mbr2Gpt_" + code.ToString(CultureInfo.InvariantCulture) : "Partition_Mbr2Gpt_Unknown";

    public static string LastToolLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("MBR2GPT:", StringComparison.OrdinalIgnoreCase)) ?? "";

    public static bool BootCodeWritten(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after) =>
        before.Length >= 512 && after.Length >= 512 && after[510] == 0x55 && after[511] == 0xAA &&
        after.Slice(446, 64).SequenceEqual(before.Slice(446, 64)) && after[..440].IndexOfAnyExcept((byte)0) >= 0;

    public static Process Start(ProcessStartInfo info)
    {
        try { return Process.Start(info) ?? throw new InvalidOperationException("Partition_Error_ToolBlocked|" + Path.GetFileName(info.FileName)); }
        catch (System.ComponentModel.Win32Exception error)
        { throw new InvalidOperationException("Partition_Error_ToolBlocked|" + Path.GetFileName(info.FileName), error); }
    }

    public static async Task<ToolResult> RunAsync(string path, string arguments, TimeSpan? timeout = null)
    {
        using var process = Start(new ProcessStartInfo
        {
            FileName = path, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (timeout is TimeSpan limit)
        {
            using var guard = new CancellationTokenSource(limit);
            try { await process.WaitForExitAsync(guard.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                throw new TimeoutException("Partition_Error_ReadTimeout");
            }
        }
        // A conversion is never killed midway: an interrupted MBR2GPT is worse than a slow one.
        else await process.WaitForExitAsync(CancellationToken.None);
        return new(process.ExitCode, await stdout + await stderr);
    }
}
