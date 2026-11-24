using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Core.Tests;

public static class SourceSearchTests
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "window-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Windows"));
            Directory.CreateDirectory(Path.Combine(root, "split"));
            File.WriteAllBytes(Path.Combine(root, "Win11.iso"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "deep.wim"), [1]);
            File.WriteAllBytes(Path.Combine(root, "Windows", "hidden.iso"), [1]);
            File.WriteAllBytes(Path.Combine(root, "split", "install.swm"), [1, 2]);
            File.WriteAllBytes(Path.Combine(root, "split", "install2.swm"), [3, 4]);
            File.WriteAllBytes(Path.Combine(root, "split", "install3.swm"), [5]);
            File.WriteAllBytes(Path.Combine(root, "split", "win11.swm"), [6, 7]);
            File.WriteAllBytes(Path.Combine(root, "split", "win112.swm"), [8, 9]);
            var results = new List<SourceCandidate>();
            await foreach (var result in SourceSearch.ScanAsync([root], [], CancellationToken.None))
            {
                results.Add(result);
            }
            Assert(results.Count == 4, "source scan candidates");
            Assert(results.Single(item => item.Path.EndsWith("install.swm", StringComparison.OrdinalIgnoreCase)).Size == 5, "install swm size");
            Assert(results.Single(item => item.Path.EndsWith("win11.swm", StringComparison.OrdinalIgnoreCase)).Size == 4, "custom swm size");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
