using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

namespace Felix.WindowSetup.UiTests;

internal static partial class PackUriAudit
{
    public static void RunAll()
    {
        WpfTestHost.EnsureApp();
        var repoRoot = FindRepoRoot();
        var felixUiRoot = Path.GetFullPath(Path.Combine(repoRoot, "..", "felix-desktop", "Felix.UI"));
        var roots = new (string Dir, string Assembly)[]
        {
            (Path.Combine(repoRoot, "src", "WindowSetup"), "WindowSetup"),
            (felixUiRoot, "Felix.UI")
        };

        var count = 0;
        foreach (var (dir, assembly) in roots)
        {
            if (!Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException($"PackUriAudit directory not found: {dir}");
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                         .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                     !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(path);
                foreach (Match m in Absolute().Matches(text))
                {
                    if (m.Value.Contains('{')) continue;
                    count++;
                    if (!Exists(m.Groups["asm"].Value, m.Groups["path"].Value))
                    {
                        throw new InvalidOperationException($"{path}: missing resource {m.Value}");
                    }
                }
                if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                var baseFolder = Path.GetRelativePath(dir, Path.GetDirectoryName(path)!).Replace('\\', '/');
                foreach (Match m in RelativeSource().Matches(text))
                {
                    var value = m.Groups[1].Value;
                    if (value.StartsWith('/') || value.Contains(':') || value.Contains('{')) continue;
                    count++;
                    var resolved = baseFolder == "." ? value : baseFolder + "/" + value;
                    if (!Exists(assembly, resolved))
                    {
                        throw new InvalidOperationException($"{path}: missing relative resource {value} (resolved: {resolved})");
                    }
                }
            }
        }

        if (count <= 20)
        {
            throw new InvalidOperationException($"PackUriAudit found only {count} references.");
        }
        Console.WriteLine($"PASS PackUriAudit ({count} references resolved)");
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Felix.WindowSetup.slnx")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException("Could not locate Felix.WindowSetup.slnx.");
    }

    private static bool Exists(string assembly, string path)
    {
        try
        {
            return Application.GetResourceStream(new Uri($"pack://application:,,,/{assembly};component/{path}")) != null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?:pack://application:,,,)?/(?<asm>(?:Felix\.[A-Za-z.]+|WindowSetup));component/(?<path>[^""'\s)]+)")]
    private static partial Regex Absolute();

    [GeneratedRegex(@"Source=""([^""]+\.xaml)""")]
    private static partial Regex RelativeSource();
}
