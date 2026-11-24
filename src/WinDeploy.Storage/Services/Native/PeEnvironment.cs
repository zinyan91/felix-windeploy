using Microsoft.Win32;

namespace Felix.Partition.Services.Native;

public static class PeEnvironment
{
    public static bool IsWinPe { get; } = Detect();

    private static bool Detect()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        return key != null;
    }

    /// <summary>A FelixPE USB carries the app library and the boot image; wiping it would destroy the running toolkit.</summary>
    public static bool IsPeMedia(string volumeRoot) =>
        IsWinPe && (File.Exists(volumeRoot + @"Apps\apps.json") || File.Exists(volumeRoot + @"sources\boot.wim"));
}
