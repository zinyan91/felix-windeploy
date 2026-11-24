using System.Diagnostics;
using System.Text;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public static class WimlibProcessRunner
{
    public static async Task<EngineExecutionResult> RunAsync(
        WimlibCommand command,
        Action<WimlibProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var start = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("wimlib-imagex could not be started.");

        var output = new StringBuilder();
        var sync = new object();

        Task ReadStreamAsync(StreamReader reader) => Task.Run(async () =>
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                lock (sync)
                {
                    output.AppendLine(line);
                }
                if (WimlibProgressParser.TryParse(line, out var progress))
                {
                    onProgress?.Invoke(progress);
                }
            }
        });

        var stdoutTask = ReadStreamAsync(process.StandardOutput);
        var stderrTask = ReadStreamAsync(process.StandardError);

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask);
        cancellationToken.ThrowIfCancellationRequested();

        return new EngineExecutionResult(process.ExitCode, output.ToString());
    }
}
