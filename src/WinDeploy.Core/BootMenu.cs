using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public sealed record BcdEntry(string Identifier, string Description, string Device, string OsDevice);

public static partial class BootMenu
{
    public static IReadOnlyList<BcdEntry> Parse(string enumAllVerboseOutput)
    {
        var entries = new List<BcdEntry>();
        string? identifier = null;
        string description = "";
        string device = "";
        string osDevice = "";
        foreach (var line in enumAllVerboseOutput.Split(['\r', '\n']))
        {
            if (line.StartsWith("identifier", StringComparison.OrdinalIgnoreCase))
            {
                Add();
                identifier = Value(line);
                description = "";
                device = "";
                osDevice = "";
            }
            else if (line.StartsWith("description", StringComparison.OrdinalIgnoreCase))
            {
                description = Value(line);
            }
            else if (line.StartsWith("device", StringComparison.OrdinalIgnoreCase))
            {
                device = Value(line);
            }
            else if (line.StartsWith("osdevice", StringComparison.OrdinalIgnoreCase))
            {
                osDevice = Value(line);
            }
        }
        Add();
        return entries;

        void Add()
        {
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                entries.Add(new(identifier, description, device, osDevice));
            }
        }

        static string Value(string line)
        {
            var index = line.IndexOf(':');
            if (index >= 0)
            {
                return line[(index + 1)..].Trim();
            }
            var firstSpace = line.IndexOfAny([' ', '\t']);
            return firstSpace < 0 ? "" : line[firstSpace..].Trim();
        }
    }

    public static IReadOnlyList<BcdEntry> Missing(
        IReadOnlyList<BcdEntry> before,
        IReadOnlyList<BcdEntry> after,
        string formattedDevice)
    {
        var afterIds = after.Select(entry => entry.Identifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return before.Where(entry =>
                !afterIds.Contains(entry.Identifier) &&
                !entry.Device.Equals(formattedDevice, StringComparison.OrdinalIgnoreCase) &&
                !entry.OsDevice.Equals(formattedDevice, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
