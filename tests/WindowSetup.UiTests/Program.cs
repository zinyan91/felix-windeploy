using Felix.WindowSetup;
using Felix.WindowSetup.Services;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant();
        switch (mode)
        {
            case "--viewmodels":
                ViewModelTests.RunAsync().GetAwaiter().GetResult();
                break;
            case "--layout":
                LayoutProbe.Run();
                break;
            case "--capture":
                var dir = args.Length > 1 ? args[1] : throw new ArgumentException("Capture directory is required.");
                LayoutProbe.Run(dir);
                break;
            case "--packuri":
                PackUriAudit.RunAll();
                break;
            case "--wimlib":
                WimlibEndToEndTests.RunAsync().GetAwaiter().GetResult();
                break;
            case "--live-smoke":
                RunLiveSmokeAsync().GetAwaiter().GetResult();
                break;
            default:
                ViewModelTests.RunAsync().GetAwaiter().GetResult();
                LayoutProbe.Run();
                PackUriAudit.RunAll();
                WimlibEndToEndTests.RunAsync().GetAwaiter().GetResult();
                break;
        }

        Console.WriteLine("Window Setup UI tests passed.");
        return 0;
    }

    private static async Task RunLiveSmokeAsync()
    {
        var targetSource = new NativeTargetSource();
        var hwVm = new HardwareCheckViewModel(new NativeHardwareProfileService(targetSource));
        await hwVm.RefreshAsync(isVietnamese: true);
        Console.WriteLine($"HW CPU: {hwVm.CpuName} ({hwVm.CpuDetail})");
        Console.WriteLine($"HW RAM: {hwVm.RamTotal} ({hwVm.RamDetail})");
        Console.WriteLine($"HW GPU: {hwVm.GpuName} ({hwVm.GpuDetail})");
        Console.WriteLine($"HW Board/FW: {hwVm.BoardName} ({hwVm.FirmwareDetail})");
        Console.WriteLine($"HW Primary: {hwVm.PrimaryEdition} | {hwVm.PrimaryReason}");
        foreach (var check in hwVm.Checks)
        {
            Console.WriteLine($"  Check [{check.Severity}] {check.Label}: {check.ValueText}");
        }

        if (hwVm.IsLoading || !string.IsNullOrEmpty(hwVm.ErrorMessage) || hwVm.Checks.Count != 6)
        {
            throw new InvalidOperationException($"HardwareCheckViewModel live smoke failed: IsLoading={hwVm.IsLoading}, Error={hwVm.ErrorMessage}, Checks={hwVm.Checks.Count}");
        }

        var findVm = new FindSourceViewModel(new NativeSourceScanner(targetSource), new WindowsSourceInspector());
        await findVm.StartAsync();
        Console.WriteLine($"FindSource HasError={findVm.HasError} ErrorMessage={findVm.ErrorMessage ?? "<null>"} Results={findVm.Results.Count}");
        foreach (var row in findVm.Results)
        {
            Console.WriteLine($"  ISO: {row.Candidate.Path} | Editions={row.EditionCount} | {row.SummaryText}");
        }

        if (findVm.HasError
            || findVm.Results.Count < 2
            || findVm.Results.Any(r => r.Candidate.Path.Contains("Anhdv", StringComparison.OrdinalIgnoreCase))
            || findVm.Results.Any(r => r.Candidate.Path.Contains("felixpe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"FindSourceViewModel live smoke failed: HasError={findVm.HasError}, Error={findVm.ErrorMessage}, Count={findVm.Results.Count}");
        }

        Console.WriteLine("PASS LiveSmoke");
    }
}
