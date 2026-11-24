using System.Globalization;
using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public enum WimlibProgressPhase
{
    Unknown,
    Verify,
    CreatingFiles,
    ExtractingData,
    ApplyingMetadata
}

public sealed record WimlibProgress(
    WimlibProgressPhase Phase,
    int PhasePercent,
    int OverallPercent,
    string RawLine);

public static partial class WimlibProgressParser
{
    public static bool TryParse(string? line, out WimlibProgress progress)
    {
        progress = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = PercentDonePattern().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawPercent))
        {
            return false;
        }

        var phasePercent = Math.Clamp(rawPercent, 0, 100);
        var (phase, overall) = line switch
        {
            _ when line.Contains("Creating files:", StringComparison.OrdinalIgnoreCase) =>
                (WimlibProgressPhase.CreatingFiles, (int)Math.Round(phasePercent * 0.10)),
            _ when line.Contains("Extracting file data:", StringComparison.OrdinalIgnoreCase) =>
                (WimlibProgressPhase.ExtractingData, 10 + (int)Math.Round(phasePercent * 0.80)),
            _ when line.Contains("Applying metadata to files:", StringComparison.OrdinalIgnoreCase) =>
                (WimlibProgressPhase.ApplyingMetadata, 90 + (int)Math.Round(phasePercent * 0.10)),
            _ when line.Contains("Verifying file data:", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("Verifying integrity", StringComparison.OrdinalIgnoreCase) =>
                (WimlibProgressPhase.Verify, phasePercent),
            _ => (WimlibProgressPhase.Unknown, phasePercent)
        };

        progress = new WimlibProgress(phase, phasePercent, Math.Clamp(overall, 0, 100), line.Trim());
        return true;
    }

    [GeneratedRegex(@"\((\d{1,3})%\)\s*done", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PercentDonePattern();
}
