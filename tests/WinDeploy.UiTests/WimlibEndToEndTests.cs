using System.IO;
using System.Text;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.UiTests;

internal static class WimlibEndToEndTests
{
    public static async Task RunAsync()
    {
        var wimlib = WindowSetupEngineLocator.Find();
        var root = Path.Combine(Path.GetTempPath(), "felix-wimlib-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var srcDir = Path.Combine(root, "src");
            var destDir = Path.Combine(root, "dest");
            var sys32 = Path.Combine(srcDir, "Windows", "System32");
            Directory.CreateDirectory(sys32);
            Directory.CreateDirectory(destDir);

            var payloadBytes = Encoding.UTF8.GetBytes("Felix Window Setup wimlib verification payload 1234567890\n");
            var srcFile = Path.Combine(sys32, "hello.txt");
            await File.WriteAllBytesAsync(srcFile, payloadBytes);

            var wimPath = Path.Combine(root, "test.wim");
            var captureResult = await WimlibProcessRunner.RunAsync(
                new WimlibCommand(wimlib, ["capture", srcDir, wimPath, "Test Edition"]));
            Assert(captureResult.ExitCode == 0, $"wimlib capture failed with code {captureResult.ExitCode}: {captureResult.Output}");

            var verifyProgress = new List<WimlibProgress>();
            var verifyResult = await WimlibProcessRunner.RunAsync(
                WimlibCommandBuilder.CreateVerify(wimlib, wimPath),
                verifyProgress.Add);
            Assert(verifyResult.ExitCode == 0, $"wimlib verify failed with code {verifyResult.ExitCode}: {verifyResult.Output}");

            var applyProgress = new List<WimlibProgress>();
            var applyResult = await WimlibProcessRunner.RunAsync(
                WimlibCommandBuilder.CreateApply(wimlib, wimPath, 1, destDir),
                applyProgress.Add);
            Assert(applyResult.ExitCode == 0, $"wimlib apply failed with code {applyResult.ExitCode}: {applyResult.Output}");
            Assert(applyProgress.Count > 0, "Expected wimlib apply progress callbacks.");

            var destFile = Path.Combine(destDir, "Windows", "System32", "hello.txt");
            Assert(File.Exists(destFile), "Applied file missing from destination.");
            var actualBytes = await File.ReadAllBytesAsync(destFile);
            Assert(actualBytes.SequenceEqual(payloadBytes), "Applied file content mismatch.");

            // Corrupt a byte near the middle of the WIM and verify failure is detected.
            var wimBytes = await File.ReadAllBytesAsync(wimPath);
            Assert(wimBytes.Length > 128, "Generated WIM unexpectedly small.");
            var corruptIndex = wimBytes.Length / 2;
            wimBytes[corruptIndex] ^= 0xFF;
            await File.WriteAllBytesAsync(wimPath, wimBytes);

            var corruptVerify = await WimlibProcessRunner.RunAsync(
                WimlibCommandBuilder.CreateVerify(wimlib, wimPath));
            Assert(corruptVerify.ExitCode != 0, "Corrupted WIM unexpectedly passed verification.");

            Console.WriteLine("PASS WimlibEndToEndTests");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
