using System.IO.Compression;
using System.Security.Cryptography;

namespace Felix.WindowSetup.Core;

public enum RemoteSupportAppKind
{
    AnyDesk,
    RustDesk,
    TeamViewerPortable,
    UltraViewer
}

public sealed record RemoteAppSelection(
    RemoteSupportAppKind Kind,
    bool Enabled,
    string Password = "");

public enum FirstDesktopAppMode
{
    None,
    PortableExe,
    ZipArchive
}

public sealed record FirstDesktopAppOptions(
    FirstDesktopAppMode Mode = FirstDesktopAppMode.None,
    string? SourcePath = null,
    string? ProgramInsideZip = null);

public sealed record FirstBootOptions(
    IReadOnlyList<RemoteAppSelection>? RemoteApps = null,
    FirstDesktopAppOptions? FirstDesktopApp = null,
    bool InstallSilently = true,
    bool WriteDesktopSummaryReport = true)
{
    public bool HasEnabledRemoteApps =>
        RemoteApps is not null && RemoteApps.Any(app => app.Enabled);

    public bool HasFirstDesktopApp =>
        FirstDesktopApp is { Mode: not FirstDesktopAppMode.None }
        && !string.IsNullOrWhiteSpace(FirstDesktopApp.SourcePath);

    public bool HasPreFormatStagingWork =>
        HasEnabledRemoteApps || HasFirstDesktopApp;

    public bool HasOfflineWork =>
        HasEnabledRemoteApps || HasFirstDesktopApp || WriteDesktopSummaryReport;
}

public static class RemoteSupportCatalog
{
    public static bool RequiresPassword(RemoteSupportAppKind kind) =>
        kind != RemoteSupportAppKind.TeamViewerPortable;

    public static string DisplayName(RemoteSupportAppKind kind) => kind switch
    {
        RemoteSupportAppKind.AnyDesk => "AnyDesk",
        RemoteSupportAppKind.RustDesk => "RustDesk",
        RemoteSupportAppKind.TeamViewerPortable => "TeamViewer Portable",
        RemoteSupportAppKind.UltraViewer => "UltraViewer",
        _ => kind.ToString()
    };

    public static string DownloadUrl(RemoteSupportAppKind kind) => kind switch
    {
        RemoteSupportAppKind.AnyDesk => "https://download.anydesk.com/AnyDesk.exe",
        RemoteSupportAppKind.RustDesk => "https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-1.3.7-x86_64.exe",
        RemoteSupportAppKind.TeamViewerPortable => "https://download.teamviewer.com/download/TeamViewerPortable.zip",
        RemoteSupportAppKind.UltraViewer => "https://www.ultraviewer.net/en/UltraViewer_setup_6.6_en.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string InstallerFileName(RemoteSupportAppKind kind) => kind switch
    {
        RemoteSupportAppKind.AnyDesk => "AnyDesk.exe",
        RemoteSupportAppKind.RustDesk => "rustdesk-setup.exe",
        RemoteSupportAppKind.TeamViewerPortable => "TeamViewerPortable.zip",
        RemoteSupportAppKind.UltraViewer => "UltraViewer_setup.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public static class RemotePasswordPolicy
{
    private const string AllowedPunctuation = "!@#$*_-+=.?~";

    public static string Generate()
    {
        Span<char> digits = stackalloc char[9];
        for (var i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return "FLX" + new string(digits);
    }

    public static (bool IsValid, string? Error) Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 64)
        {
            return (false, "Password must be between 8 and 64 characters.");
        }

        var hasLetter = false;
        foreach (var ch in password)
        {
            if (char.IsWhiteSpace(ch))
            {
                return (false, "Password cannot contain spaces.");
            }

            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                hasLetter = true;
                continue;
            }

            if (ch is >= '0' and <= '9' || AllowedPunctuation.IndexOf(ch) >= 0)
            {
                continue;
            }

            return (false, "Password contains unsupported characters.");
        }

        return hasLetter
            ? (true, null)
            : (false, "Password must contain at least one letter.");
    }
}

public static class ZipProgramInspector
{
    public static IReadOnlyList<string> ListExecutables(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return [];
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name)
                    && entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.FullName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
