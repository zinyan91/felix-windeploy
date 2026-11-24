using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public enum SourceImageKind
{
    Iso,
    Wim,
    Esd,
    Swm
}

public sealed record SwmSet(
    string FirstPart,
    IReadOnlyList<string> Parts,
    IReadOnlyList<int> MissingParts)
{
    public bool IsComplete => MissingParts.Count == 0;
}

public static class SourceImages
{
    public static SourceImageKind? KindOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".iso" => SourceImageKind.Iso,
            ".wim" => SourceImageKind.Wim,
            ".esd" => SourceImageKind.Esd,
            ".swm" => SourceImageKind.Swm,
            _ => null
        };

    public static SwmSet ResolveSwm(string firstPart, IEnumerable<string> filesInDirectory)
    {
        var fullFirst = Path.GetFullPath(firstPart);
        var directory = Path.GetDirectoryName(fullFirst) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fullFirst);
        var files = filesInDirectory
            .Select(Path.GetFullPath)
            .Where(file => string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (files.Contains(fullFirst))
        {
            parts.Add(fullFirst);
        }

        var numbered = new Dictionary<int, string>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = name[stem.Length..];
            if (int.TryParse(suffix, out var number) && number >= 2)
            {
                numbered[number] = file;
            }
        }

        var max = numbered.Keys.DefaultIfEmpty(1).Max();
        var missing = new List<int>();
        for (var number = 2; number <= max; number++)
        {
            if (numbered.TryGetValue(number, out var file))
            {
                parts.Add(file);
            }
            else
            {
                missing.Add(number);
            }
        }

        return new SwmSet(fullFirst, parts, missing);
    }

    public static string? ReleaseName(int build) => build switch
    {
        19044 => "21H2",
        19045 => "22H2",
        22000 => "21H2",
        22621 => "22H2",
        22631 => "23H2",
        26100 => "24H2",
        26200 => "25H2",
        _ => null
    };

    public static IReadOnlySet<char> SetupFileLetters(string? imagePath, string appDirectory, string? answerPath)
    {
        var letters = new HashSet<char>();
        AddPath(imagePath);
        AddPath(appDirectory);
        AddPath(answerPath);
        if (KindOf(imagePath ?? string.Empty) == SourceImageKind.Swm && imagePath is not null)
        {
            var directory = Path.GetDirectoryName(imagePath);
            if (directory is not null && Directory.Exists(directory))
            {
                foreach (var part in Directory.EnumerateFiles(directory, "*.swm"))
                {
                    AddPath(part);
                }
            }
        }

        return letters;

        void AddPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root) && char.IsLetter(root[0]))
            {
                letters.Add(char.ToUpperInvariant(root[0]));
            }
        }
    }

    private static readonly Regex SwmName = new(@"^(?<stem>.+?)(?<number>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

public static class SetupFileLetters
{
    public static IReadOnlySet<char> Of(string? imagePath, string appDirectory, string? answerPath) =>
        SourceImages.SetupFileLetters(imagePath, appDirectory, answerPath);
}
