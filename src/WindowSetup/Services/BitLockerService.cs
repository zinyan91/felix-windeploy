using System.Diagnostics;
using Felix.Partition.Services.Native;

namespace Felix.WindowSetup.Services;

public sealed record BitLockerActionResult(bool Succeeded, string Output);

public interface IBitLockerService
{
    Task<BitLockerActionResult> UnlockWithPasswordAsync(char driveLetter, string password, CancellationToken cancellationToken = default);
    Task<BitLockerActionResult> UnlockWithRecoveryKeyAsync(char driveLetter, string recoveryKey, CancellationToken cancellationToken = default);
    Task<BitLockerActionResult> TurnOffEncryptionAsync(char driveLetter, CancellationToken cancellationToken = default);
}

public sealed class NativeBitLockerService : IBitLockerService
{
    public async Task<BitLockerActionResult> UnlockWithPasswordAsync(
        char driveLetter,
        string password,
        CancellationToken cancellationToken = default)
    {
        var manageBde = SystemTools.Find("manage-bde.exe");
        if (string.IsNullOrEmpty(manageBde))
        {
            return new BitLockerActionResult(false, "manage-bde.exe was not found.");
        }

        var drive = $"{char.ToUpperInvariant(driveLetter)}:";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = manageBde,
            Arguments = $"-unlock {drive} -pw",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return new BitLockerActionResult(false, "Failed to start manage-bde.exe.");
        }

        await process.StandardInput.WriteLineAsync(password.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var combined = $"{stdout}{Environment.NewLine}{stderr}".Trim();
        return new BitLockerActionResult(process.ExitCode == 0, combined);
    }

    public async Task<BitLockerActionResult> UnlockWithRecoveryKeyAsync(
        char driveLetter,
        string recoveryKey,
        CancellationToken cancellationToken = default)
    {
        var manageBde = SystemTools.Find("manage-bde.exe");
        if (string.IsNullOrEmpty(manageBde))
        {
            return new BitLockerActionResult(false, "manage-bde.exe was not found.");
        }

        var drive = $"{char.ToUpperInvariant(driveLetter)}:";
        var res = await SystemTools.RunAsync(manageBde, ["-unlock", drive, "-recoverypassword", recoveryKey.Trim()]);
        return new BitLockerActionResult(res.ExitCode == 0, res.Output.Trim());
    }

    public async Task<BitLockerActionResult> TurnOffEncryptionAsync(
        char driveLetter,
        CancellationToken cancellationToken = default)
    {
        var manageBde = SystemTools.Find("manage-bde.exe");
        if (string.IsNullOrEmpty(manageBde))
        {
            return new BitLockerActionResult(false, "manage-bde.exe was not found.");
        }

        var drive = $"{char.ToUpperInvariant(driveLetter)}:";
        var res = await SystemTools.RunAsync(manageBde, ["-off", drive]);
        return new BitLockerActionResult(res.ExitCode == 0, res.Output.Trim());
    }
}
