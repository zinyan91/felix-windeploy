using System.Text.RegularExpressions;

namespace Felix.WindowSetup.Core;

public sealed record SourceCandidate(string Path, SourceImageKind Kind, long Size);

public static partial class SourceSearch
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Recovery",
        "PerfLogs",
        "MSOCache",
        "AppData",
        "$Recycle.Bin",
        "System Volume Information",
        "$WINDOWS.~BT",
        "$WINDOWS.~WS",
        "FelixWindowSetup",
        "node_modules",
        ".git",
        ".next",
        ".vs",
        ".nuget",
        ".cache",
        ".gemini",
        ".codex",
        ".claude",
        ".tmp",
        ".tmp-preview",
        "obj"
    };

    private static readonly HashSet<string> PriorityDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users",
        "Downloads",
        "Desktop",
        "Compressed",
        "ISO",
        "ISOs",
        "WindowsISO",
        "Setup",
        "FelixBackups"
    };

    public static bool SkipDirectory(string name) => SkippedDirectories.Contains(name);

    public static bool IsExcludedImage(string path, IReadOnlyCollection<string> peMediaRoots)
    {
        var file = Path.GetFileName(path);
        if (file.Equals("boot.wim", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("winre.wim", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("felixpe.wim", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("pe-base.wim", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var full = Path.GetFullPath(path);
        if (peMediaRoots.Any(root => IsUnder(full, root)))
        {
            return true;
        }

        if (SourceImages.KindOf(full) != SourceImageKind.Swm)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            return IsSecondarySwmPart(full, Directory.EnumerateFiles(directory, "*.swm"));
        }

        var match = SecondarySwmName().Match(Path.GetFileNameWithoutExtension(full));
        return match.Success && int.TryParse(match.Groups["num"].Value, out var part) && part >= 2;
    }

    public static bool IsSecondarySwmPart(string swmPath, IEnumerable<string> siblingSwmFiles)
    {
        var targetName = Path.GetFileNameWithoutExtension(swmPath);
        foreach (var sibling in siblingSwmFiles)
        {
            var siblingName = Path.GetFileNameWithoutExtension(sibling);
            if (targetName.Length <= siblingName.Length ||
                !targetName.StartsWith(siblingName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = targetName[siblingName.Length..];
            if (int.TryParse(suffix, out var number) && number >= 2)
            {
                return true;
            }
        }
        return false;
    }

    public static async IAsyncEnumerable<SourceCandidate> ScanAsync(
        IEnumerable<string> roots,
        IReadOnlyCollection<string> peMediaRoots,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedPeRoots = peMediaRoots
            .Select(NormalizeDriveOrFolderRoot)
            .Where(r => !string.IsNullOrEmpty(r))
            .ToArray();

        foreach (var root in roots)
        {
            await foreach (var candidate in ScanRoot(root, normalizedPeRoots, cancellationToken))
            {
                yield return candidate;
            }
        }
    }

    private static async IAsyncEnumerable<SourceCandidate> ScanRoot(
        string root,
        IReadOnlyCollection<string> peMediaRoots,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        if (Directory.Exists(root))
        {
            pending.Push(Path.GetFullPath(root));
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            var swmFiles = files
                .Where(path => Path.GetExtension(path).Equals(".swm", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceCandidate? candidate = null;
                try
                {
                    var kind = SourceImages.KindOf(file);
                    if (kind is null || IsExcludedImage(file, peMediaRoots))
                    {
                        continue;
                    }
                    if (kind == SourceImageKind.Swm && IsSecondarySwmPart(file, swmFiles))
                    {
                        continue;
                    }

                    var size = new FileInfo(file).Length;
                    if (kind == SourceImageKind.Swm)
                    {
                        var set = SourceImages.ResolveSwm(file, swmFiles);
                        if (!set.IsComplete)
                        {
                            continue;
                        }
                        size = set.Parts.Sum(part => new FileInfo(part).Length);
                    }
                    candidate = new SourceCandidate(file, kind.Value, size);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }

                if (candidate is not null)
                {
                    yield return candidate;
                }
            }

            try
            {
                var deferred = new List<string>();
                var prioritized = new List<string>();
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || SkipDirectory(info.Name))
                    {
                        continue;
                    }

                    if (PriorityDirectories.Contains(info.Name))
                    {
                        prioritized.Add(directory);
                    }
                    else
                    {
                        deferred.Add(directory);
                    }
                }

                foreach (var dir in deferred)
                {
                    pending.Push(dir);
                }
                foreach (var dir in prioritized)
                {
                    pending.Push(dir);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
            await Task.Yield();
        }
    }

    private static string NormalizeDriveOrFolderRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var trimmed = root.Trim();
        var clean = trimmed.TrimEnd('\\', ':');
        if (clean.Length == 1 && char.IsLetter(clean[0]))
        {
            return $@"{char.ToUpperInvariant(clean[0])}:\";
        }

        return trimmed;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(?<stem>.+?)(?<num>\d+)$", RegexOptions.Compiled)]
    private static partial Regex SecondarySwmName();
}
