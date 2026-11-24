using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Core.Tests;

public static class SourceImageTests
{
    public static void Run()
    {
        Assert(SourceImages.KindOf(@"E:\a\install.SWM") == SourceImageKind.Swm, "swm kind");
        Assert(SourceImages.KindOf(@"E:\a\setup.txt") is null, "unknown kind");
        var set = SourceImages.ResolveSwm(@"E:\s\install.swm",
            [@"E:\s\install.swm", @"E:\s\install2.swm", @"E:\s\install4.swm"]);
        Assert(!set.IsComplete && set.MissingParts.SequenceEqual([3]), "missing middle part");
        Assert(SourceImages.ReleaseName(22631) == "23H2", "release name");
        Assert(DismWimInfoParser.ParseIndex("Architecture : x64\r\nVersion : 10.0.22631\r\nServicePack Build : 6060\r\n")
            == ("x64", "10.0.22631", 6060), "index parse");
        Assert(new WindowsImageEdition(5, "Windows 11 Pro", "x64", "10.0.22631", 6060).DisplayName ==
            "5 · Windows 11 Pro · x64 · 23H2 (22631.6060)", "edition display");
        var letters = SetupFileLetters.Of(@"E:\s\install.swm", @"C:\app\", @"F:\a.xml");
        Assert(letters.SetEquals(['C', 'E', 'F']), "setup file letters");

        var editions = new[]
        {
            new WindowsImageEdition(1, "Windows 11 Home", "x64", "10.0.26100", 1),
            new WindowsImageEdition(2, "Windows 11 Pro Education", "x64", "10.0.26100", 1),
            new WindowsImageEdition(3, "Windows 11 Pro", "x64", "10.0.26100", 1)
        };
        Assert(WindowsImageEditions.PickDefault(editions)?.Index == 3, "pick default Pro edition");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
