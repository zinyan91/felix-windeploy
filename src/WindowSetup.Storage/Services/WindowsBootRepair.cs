namespace Felix.Partition.Services;

public sealed record BootToolCommand(string Tool, IReadOnlyList<string> Arguments);
public sealed record WindowsBootRepairPlan(
    char WindowsLetter,
    char SystemLetter,
    string Firmware,
    bool ClearBootMenu = false,
    bool PreferBootEx = false)
{
    public string WindowsRoot => WindowsLetter + @":\";
    public string SystemRoot => SystemLetter + @":\";
    public string Store => SystemRoot + (Firmware == "UEFI" ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD");
    public string LoaderPath => @"\Windows\system32\winload." + (Firmware == "UEFI" ? "efi" : "exe");
    public string ManagerPath => Firmware == "UEFI" ? @"\EFI\Microsoft\Boot\bootmgfw.efi" : @"\bootmgr";

    public BootToolCommand BuildBcdBootCommand(bool useBootEx, string? toolPath = null)
    {
        if (WindowsLetter is < 'D' or > 'Z' || SystemLetter is < 'D' or > 'Z' || Firmware is not ("BIOS" or "UEFI"))
            throw new InvalidOperationException("Partition_Error_MigrationBoot");
        var args = new List<string> { WindowsRoot + "Windows", "/s", SystemLetter + ":", "/f", Firmware };
        if (useBootEx && Firmware == "UEFI")
        {
            args.Add("/bootex");
        }
        if (ClearBootMenu)
        {
            args.Add("/c");
        }
        args.Add("/v");
        return new(string.IsNullOrWhiteSpace(toolPath) ? "bcdboot.exe" : toolPath, args);
    }

    public IReadOnlyList<BootToolCommand> Commands()
    {
        if (WindowsLetter is < 'D' or > 'Z' || SystemLetter is < 'D' or > 'Z' || Firmware is not ("BIOS" or "UEFI"))
            throw new InvalidOperationException("Partition_Error_MigrationBoot");
        var commands = new List<BootToolCommand>
        {
            BuildBcdBootCommand(useBootEx: false),
            Set("{bootmgr}", "device", "partition=" + SystemLetter + ":"),
            Set("{bootmgr}", "path", ManagerPath),
            Set("{default}", "device", "partition=" + WindowsLetter + ":"),
            Set("{default}", "osdevice", "partition=" + WindowsLetter + ":"),
            Set("{default}", "systemroot", @"\Windows"),
            Set("{default}", "path", LoaderPath)
        };
        if (Firmware == "BIOS") commands.Add(new("bootsect.exe", ["/nt60", SystemLetter + ":", "/mbr"]));
        return commands;
    }

    private BootToolCommand Set(string entry, string property, string value) =>
        new("bcdedit.exe", ["/store", Store, "/set", entry, property, value]);
}

public interface IWindowsBootTarget
{
    Task<(int ExitCode, string Output)> RunAsync(BootToolCommand command);
    bool HasFile(string path);
}

public static class WindowsBootRepair
{
    public static async Task ApplyAsync(IWindowsBootTarget target, WindowsBootRepairPlan plan, CancellationToken token)
    {
        var commands = plan.Commands();
        for (var i = 0; i < commands.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == 0 && plan.Firmware == "UEFI" && plan.PreferBootEx)
            {
                var targetBcdBoot = Path.Combine(plan.WindowsRoot, "Windows", "System32", "bcdboot.exe");
                var bootExCmd = plan.BuildBcdBootCommand(
                    useBootEx: true,
                    toolPath: target.HasFile(targetBcdBoot) ? targetBcdBoot : null);
                var bootExResult = await target.RunAsync(bootExCmd);
                if (bootExResult.ExitCode == 0)
                {
                    continue;
                }
            }

            var command = commands[i];
            var result = await target.RunAsync(command);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Partition_Error_MigrationTool|" + command.Tool + Environment.NewLine + result.Output.Trim());
        }
        token.ThrowIfCancellationRequested();
        var required = new List<string>
        {
            plan.Store, plan.SystemRoot.TrimEnd('\\') + plan.ManagerPath,
            plan.WindowsRoot.TrimEnd('\\') + plan.LoaderPath
        };
        if (plan.Firmware == "UEFI") required.Add(plan.SystemRoot + @"EFI\Boot\bootx64.efi");
        var missing = required.Where(path => !target.HasFile(path)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Partition_Error_MigrationBoot|Missing=" +
                string.Join("|", missing));
        foreach (var entry in new[] { "{bootmgr}", "{default}" })
        {
            var result = await target.RunAsync(new("bcdedit.exe", ["/store", plan.Store, "/enum", entry, "/v"]));
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !VerifiedEntry(result.Output, entry == "{default}", plan))
                throw new InvalidOperationException("Partition_Error_MigrationBoot|Entry=" + entry +
                    Environment.NewLine + result.Output.Trim());
        }
    }

    public static bool VerifiedEntry(string output, bool loader, WindowsBootRepairPlan plan)
    {
        var tokens = output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var device = "partition=" + (loader ? plan.WindowsLetter : plan.SystemLetter) + ":";
        var partitions = tokens.Where(t => t.StartsWith("partition=", StringComparison.OrdinalIgnoreCase)).ToArray();
        var relativeBootDevice = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Any(fields => fields.Length > 1 && fields[^1].Equals("boot", StringComparison.OrdinalIgnoreCase));
        return partitions.Length == (loader ? 2 : 1) && partitions.All(t => t.Equals(device, StringComparison.OrdinalIgnoreCase)) &&
            tokens.Any(t => Guid.TryParse(t, out _)) &&
            tokens.Count(t => t.Equals(loader ? plan.LoaderPath : plan.ManagerPath, StringComparison.OrdinalIgnoreCase)) == 1 &&
            (!loader || tokens.Count(t => t.Equals(@"\Windows", StringComparison.OrdinalIgnoreCase)) == 1) &&
            !relativeBootDevice &&
            !tokens.Any(t => t.StartsWith("vhd=", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("ramdisk=", StringComparison.OrdinalIgnoreCase) || t.StartsWith("locate=", StringComparison.OrdinalIgnoreCase));
    }
}
