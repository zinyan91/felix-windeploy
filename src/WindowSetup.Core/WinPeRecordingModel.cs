namespace Felix.WindowSetup.Core;

public enum WinPeRecordMode
{
    None,
    Video,
    Screenshots,
    Both
}

public sealed record WinPeRecordingOptions(
    WinPeRecordMode Mode = WinPeRecordMode.None,
    int ScreenshotIntervalSeconds = 60)
{
    public bool IsActive => Mode != WinPeRecordMode.None;
    public bool CapturesVideo => Mode is WinPeRecordMode.Video or WinPeRecordMode.Both;
    public bool CapturesScreenshots => Mode is WinPeRecordMode.Screenshots or WinPeRecordMode.Both;
}

public static class WinPeRecordingValidator
{
    public const int MinIntervalSeconds = 10;
    public const int MaxIntervalSeconds = 3600;

    public static (bool IsValid, int TotalSeconds, string? Error) ValidateInterval(int minutes, int seconds)
    {
        if (minutes < 0 || seconds < 0 || seconds >= 60)
        {
            return (false, 0, "Minutes must be >= 0 and seconds must be 0..59.");
        }

        var total = minutes * 60 + seconds;
        if (total < MinIntervalSeconds || total > MaxIntervalSeconds)
        {
            return (false, total, "Interval must be between 10 seconds and 60 minutes.");
        }

        return (true, total, null);
    }
}
