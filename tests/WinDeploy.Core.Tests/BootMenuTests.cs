using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Core.Tests;

public static class BootMenuTests
{
    public static void Run()
    {
        var before = BootMenu.Parse("""
            identifier              {bootmgr}
            description             Windows Boot Manager
            device                  partition=S:

            identifier              {keep}
            description             Keep entry
            device                  partition=D:
            osdevice                partition=D:

            identifier              {default}
            description             Windows
            device                  partition=W:
            osdevice                partition=W:
            """);
        Assert(before.Count == 3, "bcd parse");
        var missing = BootMenu.Missing(before, before.Where(entry => entry.Identifier != "{keep}").ToArray(), "partition=W:");
        Assert(missing.Count == 1 && missing[0].Identifier == "{keep}", "missing bcd entry");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
