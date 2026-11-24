using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed class WinPeScreenRecorder : IAsyncDisposable
{
    private readonly WinPeRecordingOptions options;
    private readonly string captureSessionDir;
    private readonly CancellationTokenSource cts = new();
    private Task? screenshotLoopTask;
    private Process? ffmpegProcess;
    private string? videoPath;
    private string? ffmpegLogPath;
    private bool stopped;

    public WinPeScreenRecorder(WinPeRecordingOptions options, string workingRoot)
    {
        this.options = options;
        captureSessionDir = Path.Combine(workingRoot, $"FelixRecord-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    }

    public string SessionDirectory => captureSessionDir;

    public void Start()
    {
        if (!options.IsActive)
        {
            return;
        }

        Directory.CreateDirectory(captureSessionDir);
        if (options.CapturesScreenshots)
        {
            screenshotLoopTask = RunScreenshotLoopAsync(cts.Token);
        }

        if (options.CapturesVideo && Environment.Is64BitProcess)
        {
            StartFfmpeg();
        }
    }

    public async Task<string?> StopAndExportAsync(char? targetWindowsLetter)
    {
        if (stopped || !options.IsActive)
        {
            return null;
        }

        stopped = true;
        cts.Cancel();

        if (screenshotLoopTask is not null)
        {
            try
            {
                await screenshotLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (ffmpegProcess is { HasExited: false } proc)
        {
            try
            {
                await proc.StandardInput.WriteLineAsync("q");
                await proc.StandardInput.FlushAsync();
                if (!proc.WaitForExit(8000))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        if (ffmpegProcess is not null)
        {
            var exitCode = ffmpegProcess.HasExited ? ffmpegProcess.ExitCode : -1;
            ffmpegProcess.Dispose();
            ffmpegProcess = null;

            if (exitCode != 0 && !string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
            {
                var partialPath = Path.Combine(captureSessionDir, "video-partial.mp4");
                File.Move(videoPath, partialPath, overwrite: true);
            }
        }

        if (targetWindowsLetter is not char letter)
        {
            return captureSessionDir;
        }

        var publicDesktop = $@"{char.ToUpperInvariant(letter)}:\Users\Public\Desktop\Felix-Record";
        var exportDir = Path.Combine(publicDesktop, Path.GetFileName(captureSessionDir));
        try
        {
            Directory.CreateDirectory(exportDir);
            foreach (var file in Directory.EnumerateFiles(captureSessionDir))
            {
                File.Copy(file, Path.Combine(exportDir, Path.GetFileName(file)), overwrite: true);
            }
            return exportDir;
        }
        catch
        {
            return captureSessionDir;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!stopped)
        {
            await StopAndExportAsync(null);
        }
        cts.Dispose();
    }

    private async Task RunScreenshotLoopAsync(CancellationToken ct)
    {
        var shotIndex = 1;
        TryCaptureScreenshot(Path.Combine(captureSessionDir, $"shot-{shotIndex:0000}.png"));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.ScreenshotIntervalSeconds, 10, 3600)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            shotIndex++;
            TryCaptureScreenshot(Path.Combine(captureSessionDir, $"shot-{shotIndex:0000}.png"));
        }
    }

    private void StartFfmpeg()
    {
        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return;
        }

        videoPath = Path.Combine(captureSessionDir, "recording.mp4");
        ffmpegLogPath = Path.Combine(captureSessionDir, "ffmpeg.log");
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -f gdigrab -framerate 5 -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{videoPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!proc.Start())
            {
                proc.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await proc.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(ffmpegLogPath))
                    {
                        await File.WriteAllTextAsync(ffmpegLogPath, stderr);
                    }
                }
                catch
                {
                }
            });

            ffmpegProcess = proc;
        }
        catch
        {
        }
    }

    private static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg", "ffmpeg.exe");
        return File.Exists(bundled) ? bundled : SystemTools.Find("ffmpeg.exe");
    }

    private static void TryCaptureScreenshot(string outputPath)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return;
            }

            memDc = CreateCompatibleDC(screenDc);
            hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                return;
            }

            oldBitmap = SelectObject(memDc, hBitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, 0, 0, 0x00CC0020))
            {
                return;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var fs = File.Create(outputPath);
            encoder.Save(fs);
        }
        catch
        {
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }
            if (memDc != IntPtr.Zero)
            {
                DeleteDC(memDc);
            }
            if (screenDc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int nXDest,
        int nYDest,
        int nWidth,
        int nHeight,
        IntPtr hdcSrc,
        int nXSrc,
        int nYSrc,
        int dwRop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
