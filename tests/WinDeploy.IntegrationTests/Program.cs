using System.Text.Json;
using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup;
using Felix.WindowSetup.Core;

if (args.Length == 2 && args[0].Equals("--iso", StringComparison.OrdinalIgnoreCase))
{
    await RunIsoInspectionAsync(args[1]);
    return 0;
}

if (args.Length == 2 && args[0].Equals("--generate-unattend", StringComparison.OrdinalIgnoreCase))
{
    var xml = UnattendGenerator.Generate(new UnattendSpec(
        Username: "FelixAdmin",
        Password: "",
        ComputerName: "WS-E2E",
        TimeZone: "SE Asia Standard Time",
        Locale: "en-US",
        SkipOobe: true,
        AutoLogonOnce: true,
        ClearPasswordOnFirstLogon: false,
        BypassMicrosoftAccount: true));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    await File.WriteAllTextAsync(args[1], xml);
    Console.WriteLine($"Generated unattend at {args[1]}");
    return 0;
}

if (args.Length >= 1 && args[0].Equals("--pe-install", StringComparison.OrdinalIgnoreCase))
{
    return await RunPeInstallAsync(args);
}

var modes = args.Length == 0
    ? new[] { FirmwareMode.UefiGpt, FirmwareMode.BiosMbr }
    : new[]
    {
        args[0].Equals("MBR", StringComparison.OrdinalIgnoreCase)
            ? FirmwareMode.BiosMbr
            : FirmwareMode.UefiGpt
    };

foreach (var mode in modes)
{
    await RunLayoutAsync(mode);
}

Console.WriteLine("Window Setup VHDX layout tests passed.");
return 0;

static async Task<int> RunPeInstallAsync(string[] args)
{
    string? isoPath = null;
    string? unattendPath = null;
    string? logDir = null;
    long targetGb = 64;

    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--iso", StringComparison.OrdinalIgnoreCase)) isoPath = args[++i];
        else if (args[i].Equals("--unattend", StringComparison.OrdinalIgnoreCase)) unattendPath = args[++i];
        else if (args[i].Equals("--log-dir", StringComparison.OrdinalIgnoreCase)) logDir = args[++i];
        else if (args[i].Equals("--target-gb", StringComparison.OrdinalIgnoreCase)) targetGb = long.Parse(args[++i]);
    }

    if (string.IsNullOrWhiteSpace(isoPath) || string.IsNullOrWhiteSpace(logDir))
    {
        Console.Error.WriteLine("Usage: --pe-install --iso <path> --unattend <path> --log-dir <dir> [--target-gb 64]");
        return 2;
    }

    Directory.CreateDirectory(logDir);
    Console.WriteLine($"Inspecting source: {isoPath}");
    var inspection = await new WindowsSourceInspector().InspectAsync(isoPath);
    var edition = WindowsImageEditions.PickDefault(inspection.Editions)
        ?? throw new InvalidOperationException("No Windows edition found.");
    Console.WriteLine($"Selected edition: {edition.DisplayName}");

    var setupLetters = SetupFileLetters.Of(isoPath, AppContext.BaseDirectory, unattendPath);
    var snapshot = NativeStorageReader.Read();
    StorageSnapshotRules.Check(snapshot);

    var minSize = (targetGb - 1) * 1024L * 1024 * 1024;
    var maxSize = (targetGb + 1) * 1024L * 1024 * 1024;
    var targetDisk = snapshot.Disks.FirstOrDefault(d =>
        !d.HoldsPeMedia &&
        !d.IsReadOnly &&
        !d.IsOffline &&
        !d.IsDynamic &&
        !d.Partitions.Any(p => p.IsBoot) &&
        !d.Partitions.Any(p => !string.IsNullOrWhiteSpace(p.Letter) && setupLetters.Contains(char.ToUpperInvariant(p.Letter[0]))) &&
        d.Size >= minSize && d.Size <= maxSize)
        ?? throw new InvalidOperationException($"No eligible ~{targetGb} GB target disk found.");

    Console.WriteLine($"Selected target disk: Disk {targetDisk.Number} ({targetDisk.Model}, {targetDisk.Size / (1024.0 * 1024 * 1024):0.#} GB)");

    var request = new WindowSetupRequest(
        new WindowsSource(
            isoPath,
            edition.Index,
            edition.DisplayName,
            edition.Architecture ?? "x64",
            edition.Version ?? ""),
        new InstallTarget(
            InstallTargetMode.EraseDisk,
            string.Join("|", targetDisk.Number, targetDisk.Id, targetDisk.Serial, targetDisk.Size),
            FirmwareMode.UefiGpt,
            new("", ""),
            new("", ""),
            new("", ""),
            targetDisk.Number,
            targetDisk.Id,
            targetDisk.Serial,
            targetDisk.Size,
            targetDisk.Style,
            targetDisk.HoldsPeMedia,
            targetDisk.IsReadOnly,
            targetDisk.IsOffline,
            targetDisk.IsDynamic,
            targetDisk.Model,
            targetDisk.Bus),
        unattendPath,
        WindowSetupInputFiles.Inspect(isoPath, unattendPath),
        new InstallOptions(
            VerifyImage: false,
            CreateRecovery: true,
            ClearBootMenu: false,
            RestartWhenComplete: false,
            BypassMicrosoftAccount: true));

    var progress = new Progress<InstallProgressUpdate>(u =>
        Console.WriteLine($"[{u.Percent,3}%] {u.Stage} ({u.State}): {u.Message}"));

    var engine = WindowSetupEngineLocator.Find();
    Console.WriteLine($"Engine: {engine}");
    var runner = new WindowInstallationRunner(new WindowSetupInstallationPlatform(engine));
    var outcome = await runner.RunAsync(request, logDir, progress);

    await File.WriteAllTextAsync(
        Path.Combine(logDir, "outcome.json"),
        JsonSerializer.Serialize(outcome, InstallJournal.JsonOptions));

    if (!outcome.Succeeded)
    {
        Console.Error.WriteLine($"PE install FAILED: {outcome.Error}");
        return 1;
    }

    Console.WriteLine($"PE install SUCCEEDED. RunDirectory={outcome.RunDirectory}");
    return 0;
}

static async Task RunLayoutAsync(FirmwareMode firmware)
{
    var root = Path.Combine(Path.GetTempPath(), "FelixPE", "WindowSetupIntegration");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, $"window-setup-{firmware}-{Guid.NewGuid():N}.vhdx");
    var source = Path.Combine(root, "install.wim");
    await File.WriteAllBytesAsync(source, []);

    try
    {
        using var disk = DisposableVhdx.Create(path, 40L * 1024 * 1024 * 1024, 512);
        var snapshot = NativeStorageReader.Read();
        var target = snapshot.Disks.Single(candidate =>
            candidate.Number == disk.DiskNumber &&
            candidate.Size == disk.Size);
        var request = new WindowSetupRequest(
            new WindowsSource(source, 1, "Integration image", "x64", "test"),
            new InstallTarget(
                InstallTargetMode.EraseDisk,
                string.Join("|", target.Number, target.Id, target.Serial, target.Size),
                firmware,
                new("", ""),
                new("", ""),
                new("", ""),
                target.Number,
                target.Id,
                target.Serial,
                target.Size,
                target.Style),
            null,
            WindowSetupInputFiles.Inspect(source, null),
            new InstallOptions(CreateRecovery: true));

        var prepared = await new InstallStorageCoordinator().PrepareAsync(
            request,
            root,
            null);
        var after = NativeStorageReader.Read().Disks.Single(candidate =>
            candidate.Number == disk.DiskNumber &&
            candidate.Size == disk.Size);
        var expectedStyle = firmware == FirmwareMode.UefiGpt ? "GPT" : "MBR";
        Assert(after.Style == expectedStyle, $"Expected {expectedStyle}, found {after.Style}.");
        Assert(after.Partitions.Any(partition =>
            partition.Label.Equals("System", StringComparison.OrdinalIgnoreCase)), "System partition missing.");
        Assert(after.Partitions.Any(partition =>
            partition.Label.Equals("Windows", StringComparison.OrdinalIgnoreCase) &&
            partition.FileSystem == "NTFS"), "Windows partition missing.");
        Assert(after.Partitions.Any(partition =>
            partition.Label.Equals("Recovery", StringComparison.OrdinalIgnoreCase) &&
            partition.FileSystem == "NTFS"), "Recovery partition missing.");
        if (firmware == FirmwareMode.UefiGpt)
        {
            Assert(after.Partitions.Any(partition =>
                partition.Label.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                partition.FileSystem == "FAT32"), "UEFI System partition must be FAT32.");
        }
        else
        {
            Assert(after.Partitions.Any(partition => partition.IsActive), "BIOS System partition must be active.");
        }
        await new InstallStorageCoordinator().CleanupAsync(prepared, true);
        Console.WriteLine($"PASS {firmware}: disposable VHDX layout and filesystems verified.");
    }
    finally
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        try
        {
            File.Delete(source);
        }
        catch (IOException)
        {
        }
    }
}

static async Task RunIsoInspectionAsync(string isoPath)
{
    var opticalBefore = OpticalDriveCount();
    var inspection = await new WindowsSourceInspector().InspectAsync(isoPath);
    Assert(inspection.ImagePath == isoPath, "Inspection must keep the selected ISO path.");
    Assert(inspection.Editions.Count > 0, "ISO inspection returned no editions.");
    foreach (var edition in inspection.Editions)
    {
        Console.WriteLine($"  {edition.DisplayName} · {edition.Architecture} · {edition.Version}");
    }
    var detachDeadline = DateTime.UtcNow.AddSeconds(10);
    while (OpticalDriveCount() != opticalBefore && DateTime.UtcNow < detachDeadline)
    {
        await Task.Delay(250);
    }
    Assert(OpticalDriveCount() == opticalBefore, "ISO stayed attached after inspection.");
    Console.WriteLine($"PASS ISO inspection: {inspection.Editions.Count} editions.");
}

static int OpticalDriveCount() =>
    DriveInfo.GetDrives().Count(drive => drive.DriveType == DriveType.CDRom);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
