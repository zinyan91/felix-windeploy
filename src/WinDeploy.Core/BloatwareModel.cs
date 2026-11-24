namespace Felix.WindowSetup.Core;

public sealed record BloatwareOptions(
    bool Enabled = false,
    bool BlockPromotedApps = true,
    IReadOnlyList<string>? RemovePackagePrefixes = null)
{
    public bool HasWork => Enabled && (BlockPromotedApps || RemovePackagePrefixes is { Count: > 0 });
}

public sealed record BloatwareAppDefinition(
    string PackagePrefix,
    string DisplayName,
    bool DefaultSelected = true);

public static class BloatwareCatalog
{
    private static readonly HashSet<string> ProtectedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.StorePurchaseApp",
        "Microsoft.WindowsCalculator",
        "Microsoft.WindowsNotepad",
        "Microsoft.Paint",
        "Microsoft.ScreenSketch",
        "Microsoft.Windows.Photos",
        "Microsoft.WindowsTerminal",
        "Microsoft.SecHealthUI",
        "Microsoft.HEIFImageExtension",
        "Microsoft.VP9VideoExtensions",
        "Microsoft.WebMediaExtensions",
        "Microsoft.WebpImageExtension"
    };

    public static IReadOnlyList<BloatwareAppDefinition> DefaultApps { get; } =
    [
        new("Clipchamp.Clipchamp", "Clipchamp"),
        new("Microsoft.BingNews", "Microsoft News"),
        new("Microsoft.BingWeather", "Weather"),
        new("Microsoft.Copilot", "Copilot"),
        new("Microsoft.GamingApp", "Xbox App"),
        new("Microsoft.GetHelp", "Get Help"),
        new("Microsoft.Getstarted", "Tips"),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office Hub)"),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection"),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal"),
        new("Microsoft.OutlookForWindows", "Outlook (New)"),
        new("Microsoft.People", "People"),
        new("Microsoft.PowerAutomateDesktop", "Power Automate Desktop"),
        new("Microsoft.Todos", "Microsoft To Do"),
        new("Microsoft.WindowsAlarms", "Clock & Alarms"),
        new("Microsoft.WindowsCommunicationsApps", "Mail and Calendar"),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub"),
        new("Microsoft.WindowsMaps", "Maps"),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder"),
        new("Microsoft.Xbox.TCUI", "Xbox Live"),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar"),
        new("Microsoft.XboxGameOverlay", "Xbox Game Bar Plugin"),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider"),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox Speech To Text"),
        new("Microsoft.YourPhone", "Phone Link"),
        new("Microsoft.ZuneMusic", "Media Player"),
        new("Microsoft.ZuneVideo", "Movies & TV"),
        new("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family"),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist"),
        new("MSTeams", "Microsoft Teams")
    ];

    public static bool IsProtectedPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return true;
        }

        if (ProtectedPrefixes.Contains(prefix))
        {
            return true;
        }

        return prefix.Contains(".VCLibs.", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains(".UI.Xaml.", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains(".NET.Native.", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ParseWimWindowsAppsPaths(string wimlibDirOutput)
    {
        if (string.IsNullOrWhiteSpace(wimlibDirOutput))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string marker = @"\Program Files\WindowsApps\";
        foreach (var rawLine in wimlibDirOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var rest = line[(idx + marker.Length)..];
            var slash = rest.IndexOfAny(['\\', '/']);
            var folderName = slash >= 0 ? rest[..slash] : rest;
            var underscore = folderName.IndexOf('_');
            if (underscore <= 0 || !folderName.Contains("__", StringComparison.Ordinal))
            {
                continue;
            }

            var prefix = folderName[..underscore];
            if (!IsProtectedPrefix(prefix))
            {
                result.Add(prefix);
            }
        }

        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> ParseDismProvisionedPackages(
        string dismOutput,
        IReadOnlyCollection<string> selectedPrefixes)
    {
        if (string.IsNullOrWhiteSpace(dismOutput) || selectedPrefixes.Count == 0)
        {
            return [];
        }

        var prefixSet = new HashSet<string>(selectedPrefixes, StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();
        foreach (var rawLine in dismOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            const string key = "PackageName :";
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var packageName = trimmed[key.Length..].Trim();
            var underscore = packageName.IndexOf('_');
            var prefix = underscore > 0 ? packageName[..underscore] : packageName;
            if (!IsProtectedPrefix(prefix) && prefixSet.Contains(prefix))
            {
                matched.Add(packageName);
            }
        }

        return matched.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
