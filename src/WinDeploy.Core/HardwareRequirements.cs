namespace Felix.WindowSetup.Core;

public sealed record CpuCapabilities(bool HasPopcnt, bool HasSse42);

public static class HardwareRequirements
{
    public static int? ParseBuildNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var lastSegment = version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(lastSegment, out var build) ? build : null;
    }

    public static bool RequiresPopcntAndSse42(string? architecture, string? version)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return false;
        }

        var isX86Family = architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                          architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
                          architecture.Equals("x86", StringComparison.OrdinalIgnoreCase);
        if (!isX86Family)
        {
            return false;
        }

        var build = ParseBuildNumber(version);
        return build >= 26100;
    }
}
