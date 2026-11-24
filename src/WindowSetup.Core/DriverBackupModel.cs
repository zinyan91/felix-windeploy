using System.Globalization;

namespace Felix.WindowSetup.Core;

public enum DriverCategory
{
    Other,
    Storage,
    Network,
    Display,
    Audio,
    Bluetooth,
    System
}

public enum DriverBackupPreset
{
    None,
    Recommended,
    Minimum,
    All
}

public sealed record InstalledDriverEntry(
    string PublishedName,
    string OriginalFileName,
    bool Inbox,
    string ClassName,
    string ProviderName,
    string DateText,
    string VersionText)
{
    public string InfFileName => Path.GetFileName(OriginalFileName);

    public DriverCategory Category => DriverSelectionPolicy.Classify(this);

    public Version? ParsedVersion => DriverStoreParser.TryParseVersion(VersionText);

    public DateTime? ParsedDate => DriverStoreParser.TryParseDate(DateText);
}

public static class DriverStoreParser
{
    public static IReadOnlyList<InstalledDriverEntry> ParseDismGetDrivers(
        string dismOutput,
        bool excludeInbox = true)
    {
        if (string.IsNullOrWhiteSpace(dismOutput))
        {
            return [];
        }

        var entries = new List<InstalledDriverEntry>();
        string? published = null;
        string? original = null;
        var inbox = false;
        string? className = null;
        string? provider = null;
        string? dateText = null;
        string? versionText = null;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(published))
            {
                return;
            }

            if (!excludeInbox || !inbox)
            {
                entries.Add(new InstalledDriverEntry(
                    published.Trim(),
                    (original ?? string.Empty).Trim(),
                    inbox,
                    (className ?? string.Empty).Trim(),
                    (provider ?? string.Empty).Trim(),
                    (dateText ?? string.Empty).Trim(),
                    (versionText ?? string.Empty).Trim()));
            }

            published = null;
            original = null;
            inbox = false;
            className = null;
            provider = null;
            dateText = null;
            versionText = null;
        }

        foreach (var rawLine in dismOutput.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                Flush();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                published = value;
            }
            else if (key.Equals("Original File Name", StringComparison.OrdinalIgnoreCase))
            {
                original = value;
            }
            else if (key.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
            {
                inbox = value.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("Class Name", StringComparison.OrdinalIgnoreCase))
            {
                className = value;
            }
            else if (key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase))
            {
                provider = value;
            }
            else if (key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                dateText = value;
            }
            else if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                versionText = value;
            }
        }

        Flush();
        return entries;
    }

    public static Version? TryParseVersion(string? versionText) =>
        Version.TryParse(versionText?.Trim(), out var parsed) ? parsed : null;

    public static DateTime? TryParseDate(string? dateText) =>
        DateTime.TryParse(dateText?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.TryParse(dateText?.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : null;
}

public static class DriverSelectionPolicy
{
    private static readonly string[] CriticalStorageTokens =
    [
        "iastor",
        "vmd",
        "nvme",
        "storahci",
        "storport",
        "raid",
        "amdsata",
        "amd_sata"
    ];

    public static DriverCategory Classify(InstalledDriverEntry entry)
    {
        var cls = entry.ClassName.Trim();
        if (IsCriticalStorage(entry))
        {
            return DriverCategory.Storage;
        }

        if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("NetClient", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("NetService", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("NetTrans", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Network;
        }

        if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Display;
        }

        if (cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Sound", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Audio;
        }

        if (cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Bluetooth;
        }

        if (cls.Equals("System", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("HIDClass", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Ports", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.System;
        }

        return DriverCategory.Other;
    }

    public static bool IsCriticalStorageDriver(InstalledDriverEntry entry) =>
        IsCriticalStorage(entry);

    public static IReadOnlyList<InstalledDriverEntry> FindUnselectedCriticalStorage(
        IEnumerable<InstalledDriverEntry> allDrivers,
        IReadOnlySet<string> selectedPublishedNames)
    {
        return allDrivers
            .Where(d => IsCriticalStorage(d) && !selectedPublishedNames.Contains(d.PublishedName))
            .GroupBy(d => d.InfFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    public static bool IsCriticalStorage(InstalledDriverEntry entry)
    {
        var cls = entry.ClassName.Trim();
        if (cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("HDC", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var inf = entry.InfFileName;
        foreach (var token in CriticalStorageTokens)
        {
            if (inf.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<InstalledDriverEntry> KeepLatestOnly(
        IEnumerable<InstalledDriverEntry> entries)
    {
        var bestByInf = new Dictionary<string, InstalledDriverEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var key = string.IsNullOrWhiteSpace(entry.InfFileName)
                ? entry.PublishedName
                : entry.InfFileName;

            if (!bestByInf.TryGetValue(key, out var existing) || IsNewer(entry, existing))
            {
                bestByInf[key] = entry;
            }
        }

        return [.. bestByInf.Values];
    }

    public static IReadOnlyList<InstalledDriverEntry> SelectByPreset(
        IEnumerable<InstalledDriverEntry> entries,
        DriverBackupPreset preset,
        bool keepLatestOnly = true)
    {
        var pool = keepLatestOnly ? KeepLatestOnly(entries) : [.. entries];

        return preset switch
        {
            DriverBackupPreset.None => [],
            DriverBackupPreset.Minimum => [.. pool.Where(e =>
                e.Category is DriverCategory.Storage or DriverCategory.Network)],
            DriverBackupPreset.Recommended => [.. pool.Where(e =>
                e.Category is not DriverCategory.Display and not DriverCategory.Other)],
            DriverBackupPreset.All => pool,
            _ => []
        };
    }

    public static bool MatchesSearch(InstalledDriverEntry entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var needle = query.Trim();
        return entry.PublishedName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.OriginalFileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.ClassName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.ProviderName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.VersionText.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNewer(InstalledDriverEntry candidate, InstalledDriverEntry current)
    {
        var candVer = candidate.ParsedVersion;
        var currVer = current.ParsedVersion;
        if (candVer is not null && currVer is not null)
        {
            var cmp = candVer.CompareTo(currVer);
            if (cmp != 0)
            {
                return cmp > 0;
            }
        }
        else if (candVer is not null)
        {
            return true;
        }

        var candDate = candidate.ParsedDate;
        var currDate = current.ParsedDate;
        if (candDate.HasValue && currDate.HasValue)
        {
            return candDate.Value > currDate.Value;
        }

        return candDate.HasValue;
    }
}
