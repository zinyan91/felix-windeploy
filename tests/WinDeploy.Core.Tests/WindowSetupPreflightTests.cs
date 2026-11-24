using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Core.Tests;

public static class WindowSetupPreflightTests
{
    public static async Task Run()
    {
        var valid = WindowSetupPreflight.Validate(CreateRequest());
        Assert(valid.CanProceed, "A complete request should pass preflight.");

        var existingPlan = InstallPlanBuilder.Create(CreateRequest());
        Assert(existingPlan.Steps.Any(step => step.Kind == InstallPlanStepKind.FormatWindowsPartition),
            "Existing partition plan should format the Windows partition.");
        Assert(existingPlan.IsDestructive, "Existing partition plan should be destructive.");

        var normalInstall = WindowSetupPreflight.Validate(CreateRequest() with { AutounattendPath = null });
        Assert(normalInstall.CanProceed, "A request without an answer file should pass.");

        var rawWindowsTarget = WindowSetupPreflight.Validate(
            CreateRequest() with
            {
                Target = CreateRequest().Target with
                {
                    WindowsPartition = CreateRequest().Target.WindowsPartition with
                    {
                        FileSystem = "RAW"
                    }
                }
            });
        Assert(rawWindowsTarget.CanProceed,
            "An existing Windows target may use another file system because preparation formats it as NTFS.");

        var withoutRecovery = WindowSetupPreflight.Validate(
            CreateRequest() with
            {
                Target = CreateRequest().Target with
                {
                    RecoveryPartition = new("none", "Do not use")
                }
            });
        Assert(withoutRecovery.CanProceed, "Recovery may be skipped in existing partition mode.");

        var eraseTarget = new InstallTarget(
            InstallTargetMode.EraseDisk,
            "disk-0-samsung-s6wr-2f1",
            FirmwareMode.UefiGpt,
            new("", ""),
            new("", ""),
            new("", ""),
            DiskNumber: 0,
            DiskId: "disk-id",
            DiskSerial: "serial",
            DiskSize: 128L * 1024 * 1024 * 1024,
            DiskStyle: "GPT");
        var eraseDisk = WindowSetupPreflight.Validate(
            CreateRequest() with { Target = eraseTarget });
        Assert(eraseDisk.CanProceed, "Erase disk should create new partition identities.");
        var erasePlan = InstallPlanBuilder.Create(
            CreateRequest() with { Target = eraseTarget });
        Assert(erasePlan.Steps.Any(step => step.Kind == InstallPlanStepKind.EraseDisk),
            "Erase disk plan should include a disk erase step.");
        Assert(erasePlan.Steps.Single(step => step.Kind == InstallPlanStepKind.EraseDisk).Description == "Erase Disk 0",
            "The erase step should name the disk, not its internal identity.");
        Assert(erasePlan.Steps.Any(step => step.Kind == InstallPlanStepKind.CreateRecoveryPartition),
            "Erase disk plan should include a recovery partition.");
        var eraseWithoutRecovery = InstallPlanBuilder.Create(
            CreateRequest() with
            {
                Target = eraseTarget,
                Options = new InstallOptions(CreateRecovery: false)
            });
        Assert(!eraseWithoutRecovery.Steps.Any(step =>
                step.Kind is InstallPlanStepKind.CreateRecoveryPartition or
                    InstallPlanStepKind.ConfigureRecovery),
            "Erase disk should omit Recovery steps when Recovery is disabled.");

        var unsupportedSource = WindowSetupPreflight.Validate(
            CreateRequest() with { Source = new("C:\\setup.txt", 6, "Windows 11 Pro") });
        Assert(unsupportedSource.Issues.Any(issue => issue.Code == "source.unsupported"),
            "Unsupported source extensions should be blocked.");

        var arbitraryAnswerFile = WindowSetupPreflight.Validate(
            CreateRequest() with { AutounattendPath = "C:\\answer.xml" });
        Assert(!arbitraryAnswerFile.Issues.Any(issue => issue.Code is "answer-file.name" or "answer-file.extension"),
            "Any XML answer file name should be accepted.");

        TestInputFiles();
        TestDismOutput();
        TestWimlibCommands();
        TestWimlibProgressParser();
        TestHardwareRequirementsAndPreflight();
        TestSecureBootCheck();
        TestUnattendGenerator();
        TestDiskLayouts();
        TestDriverBackupAndWifi();
        TestBootAndRecoveryPlanning();
        TestFirstBootPlanning();
        TestDesktopHandoffPlanning();
        TestPhase1SourceBloatwareAndDrivers();
        await TestPhase2FelixWorkRescueUsbAndRecordingAsync();
        TestPhase3WindowsBackupAndHelp();
        await TestInstallationRunnerAsync();
    }

    private static void TestInputFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"window-setup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var imagePath = Path.Combine(root, "install.wim");
            var answerPath = Path.Combine(root, "Autounattend.xml");
            File.WriteAllBytes(imagePath, []);
            File.WriteAllText(answerPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><unattend xmlns=\"urn:schemas-microsoft-com:unattend\" />");

            var valid = WindowSetupInputFiles.Inspect(imagePath, answerPath);
            Assert(valid.ImageExists, "The image file should be detected.");
            Assert(valid.AnswerFileIsValid, "A valid unattend document should pass.");

            File.WriteAllText(answerPath, "<configuration />");
            var invalid = WindowSetupInputFiles.Inspect(imagePath, answerPath);
            Assert(!invalid.AnswerFileIsValid, "An answer file without an unattend root should fail.");

            var missing = WindowSetupPreflight.Validate(
                CreateRequest() with
                {
                    FileInspection = WindowSetupInputFiles.Inspect(
                        Path.Combine(root, "missing.wim"),
                        null)
                });
            Assert(missing.Issues.Any(issue => issue.Code == "source.not-found"),
                "A missing image should block preflight.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TestDismOutput()
    {
        const string output = """
            Details for image : F:\sources\install.wim

            Index : 1
            Name : Windows 11 Home
            Architecture : x64
            Version : 10.0.26100

            Index : 6
            Name : Windows 11 Pro
            Architecture : x64
            Version : 10.0.26100
            """;

        var inspection = DismWimInfoParser.Parse(@"F:\sources\install.wim", output);
        Assert(inspection.Editions.Count == 2, "Both DISM image indexes should be parsed.");
        Assert(inspection.Editions[1].Index == 6, "The image index should be preserved.");
        Assert(inspection.Editions[1].Name == "Windows 11 Pro", "The image name should be preserved.");
        Assert(inspection.Editions[1].Architecture == "x64", "The architecture should be preserved.");
    }

    private static void TestWimlibCommands()
    {
        var exe = @"C:\Tools\wimlib\x64\wimlib-imagex.exe";
        var verifyWim = WimlibCommandBuilder.CreateVerify(exe, @"F:\sources\install.wim");
        Assert(verifyWim.Arguments.SequenceEqual(["verify", @"F:\sources\install.wim"]), "Verify WIM args.");

        var verifySwm = WimlibCommandBuilder.CreateVerify(exe, @"F:\split\install.swm");
        Assert(verifySwm.Arguments.SequenceEqual(["verify", @"F:\split\install.swm", @"--ref=F:\split\install*.swm"]),
            "Verify SWM should include --ref glob.");

        var applyEsd = WimlibCommandBuilder.CreateApply(exe, @"F:\sources\install.esd", 6, @"W:\");
        Assert(applyEsd.Arguments.SequenceEqual(["apply", @"F:\sources\install.esd", "6", @"W:\"]),
            "Apply ESD args.");

        var applySwm = WimlibCommandBuilder.CreateApply(exe, @"F:\split\install.swm", 2, @"W:\");
        Assert(applySwm.Arguments.SequenceEqual(["apply", @"F:\split\install.swm", "2", @"W:\", @"--ref=F:\split\install*.swm"]),
            "Apply SWM should include --ref glob.");
    }

    private static void TestWimlibProgressParser()
    {
        Assert(WimlibProgressParser.TryParse("Verifying file data: 40 MiB of 100 MiB (40%) done", out var vp) &&
               vp.Phase == WimlibProgressPhase.Verify && vp.PhasePercent == 40 && vp.OverallPercent == 40,
            "Verify progress line should parse.");

        Assert(WimlibProgressParser.TryParse("Creating files: 500 of 1000 (50%) done", out var cp) &&
               cp.Phase == WimlibProgressPhase.CreatingFiles && cp.OverallPercent == 5,
            "Creating files should map to 0..10%.");

        Assert(WimlibProgressParser.TryParse("Extracting file data: 500 MiB of 1000 MiB (50%) done", out var ep) &&
               ep.Phase == WimlibProgressPhase.ExtractingData && ep.OverallPercent == 50,
            "Extracting data 50% should map to 50%.");

        Assert(WimlibProgressParser.TryParse("Applying metadata to files: 500 of 1000 (50%) done", out var mp) &&
               mp.Phase == WimlibProgressPhase.ApplyingMetadata && mp.OverallPercent == 95,
            "Applying metadata 50% should map to 95%.");

        Assert(cp.OverallPercent < ep.OverallPercent && ep.OverallPercent < mp.OverallPercent,
            "Overall progress should increase monotonically across apply phases.");
    }

    private static void TestHardwareRequirementsAndPreflight()
    {
        Assert(HardwareRequirements.ParseBuildNumber("10.0.26100") == 26100, "Parse build 26100.");
        Assert(HardwareRequirements.RequiresPopcntAndSse42("x64", "10.0.26100"), "26100 requires POPCNT/SSE4.2.");
        Assert(!HardwareRequirements.RequiresPopcntAndSse42("x64", "10.0.22631"), "22631 does not require POPCNT/SSE4.2.");

        var blocked = WindowSetupPreflight.Validate(CreateRequest(), new CpuCapabilities(HasPopcnt: false, HasSse42: true));
        Assert(!blocked.CanProceed && blocked.Issues.Any(i => i.Code == "source.cpu-unsupported"),
            "Build 26100 without POPCNT must block preflight.");

        var legacyReq = CreateRequest() with
        {
            Source = CreateRequest().Source with { Version = "10.0.22631" }
        };
        var allowed = WindowSetupPreflight.Validate(legacyReq, new CpuCapabilities(HasPopcnt: false, HasSse42: false));
        Assert(allowed.CanProceed, "Build 22631 should pass even without POPCNT/SSE4.2.");
    }

    private static void TestSecureBootCheck()
    {
        var off = SecureBootCheck.Evaluate(new SecureBootHostState(true, false, null, null), true);
        Assert(off.Status == SecureBootStatus.Pass && off.Code == "secureboot.off" && !off.PreferBootEx, "Secure Boot off.");

        var ready = SecureBootCheck.Evaluate(new SecureBootHostState(true, true, true, true), true);
        Assert(ready.Status == SecureBootStatus.Pass && ready.Code == "secureboot.ca2023-ready" && ready.PreferBootEx, "CA 2023 ready.");

        var missingAsset = SecureBootCheck.Evaluate(new SecureBootHostState(true, true, true, true), false);
        Assert(missingAsset.Status == SecureBootStatus.Warning && missingAsset.Code == "secureboot.ca2023-missing-asset" && !missingAsset.PreferBootEx, "Missing asset.");

        var ca2011 = SecureBootCheck.Evaluate(new SecureBootHostState(true, true, false, true), true);
        Assert(ca2011.Status == SecureBootStatus.Warning && ca2011.Code == "secureboot.ca2011-only" && !ca2011.PreferBootEx, "CA 2011 only.");

        var unreadable = SecureBootCheck.Evaluate(new SecureBootHostState(true, true, null, null), true);
        Assert(unreadable.Status == SecureBootStatus.Warning && unreadable.Code == "secureboot.db-unreadable" && !unreadable.PreferBootEx, "Unreadable DB.");
    }

    private static void TestUnattendGenerator()
    {
        var pw = UnattendGenerator.GenerateRandomPassword(12);
        Assert(pw.Length == 12 && pw.Any(char.IsUpper) && pw.Any(char.IsLower) && pw.Any(char.IsDigit),
            "Generated password must meet complexity.");

        var xml = UnattendGenerator.Generate(new UnattendSpec(
            Username: "A&B<1>",
            Password: "P@ss<1>",
            ClearPasswordOnFirstLogon: true,
            BypassMicrosoftAccount: true));
        Assert(xml.Contains("BypassNRO") && xml.Contains("AutoLogon") && xml.Contains("net user"),
            "Generated unattend must include BypassNRO, AutoLogon, and ClearPassword.");

        var bypassOnly = UnattendGenerator.GenerateBypassMicrosoftAccountOnly();
        var root = Path.Combine(Path.GetTempPath(), $"unattend-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var p1 = Path.Combine(root, "full.xml");
            var p2 = Path.Combine(root, "bypass.xml");
            File.WriteAllText(p1, xml);
            File.WriteAllText(p2, bypassOnly);
            Assert(WindowSetupInputFiles.Inspect(p1, p1).AnswerFileIsValid, "Generated full unattend XML must validate.");
            Assert(WindowSetupInputFiles.Inspect(p2, p2).AnswerFileIsValid, "Generated bypass-only unattend XML must validate.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TestDiskLayouts()
    {
        var gpt = InstallDiskLayoutBuilder.Create(
            3,
            FirmwareMode.UefiGpt,
            'S',
            'W',
            'R',
            createRecovery: true);
        Assert(gpt.Script.Contains("convert gpt"), "GPT layout should convert the target disk to GPT.");
        Assert(gpt.Script.Contains("create partition efi size=260"), "GPT layout should create a 260 MiB EFI partition.");
        Assert(gpt.Script.Contains("create partition msr size=16"), "GPT layout should create the MSR partition.");
        Assert(gpt.Script.Contains("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"), "GPT layout should set the Recovery type.");
        Assert(gpt.Script.IndexOf("assign letter=R", StringComparison.Ordinal) >
               gpt.Script.IndexOf("gpt attributes=", StringComparison.Ordinal),
            "The GPT Recovery partition should receive its temporary letter after it is marked hidden.");

        var mbr = InstallDiskLayoutBuilder.Create(
            4,
            FirmwareMode.BiosMbr,
            'S',
            'W',
            'R',
            createRecovery: true);
        Assert(mbr.Script.Contains("convert mbr"), "BIOS layout should convert the target disk to MBR.");
        Assert(mbr.Script.Contains("active"), "BIOS layout should mark the System partition active.");
        Assert(mbr.Script.Contains("set id=27 override"), "MBR layout should set the Recovery type.");

        var withoutRecovery = InstallDiskLayoutBuilder.Create(
            5,
            FirmwareMode.UefiGpt,
            'S',
            'W',
            null,
            createRecovery: false);
        Assert(!withoutRecovery.Script.Contains("label=Recovery", StringComparison.Ordinal),
            "A layout without Recovery should not create or shrink for a Recovery partition.");
    }

    private static void TestDesktopHandoffPlanning()
    {
        var request = CreateRequest();
        var stagingCandidates = new[]
        {
            new StagingVolumeCandidate('C', 80L << 30, "NTFS", false, false, true, true, false),
            new StagingVolumeCandidate('D', 12L << 30, "NTFS", false, false, true, false, true),
            new StagingVolumeCandidate('E', 50L << 30, "NTFS", true, false, false, false, false),
            new StagingVolumeCandidate('U', 60L << 30, "NTFS", false, true, false, false, false)
        };
        var existingDecision = DesktopHandoffPlanner.SelectStagingVolume(
            stagingCandidates, InstallTargetMode.ExistingPartition, 2L << 30);
        Assert(existingDecision is { Succeeded: true, SelectedLetter: 'D' },
            "ExistingPartition staging must pick non-target NTFS source partition D: and ignore target/BitLocker/USB.");

        var eraseDecision = DesktopHandoffPlanner.SelectStagingVolume(
            stagingCandidates, InstallTargetMode.EraseDisk, 2L << 30);
        Assert(!eraseDecision.Succeeded,
            "EraseDisk must reject staging volumes on the target disk and reject USB/BitLocker.");

        var uefiBcd = DesktopHandoffPlanner.BuildBcdRamdiskCommands(
            'D', @"\FelixWinDeploy\pe\boot.wim", @"\FelixWinDeploy\pe\boot.sdi",
            FirmwareMode.UefiGpt, "{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}");
        Assert(uefiBcd.Any(c => c.Contains(@"\windows\system32\boot\winload.efi"))
            && uefiBcd.Any(c => c.Contains("/bootsequence")),
            "UEFI BCD ramdisk commands must target winload.efi and /bootsequence.");

        var biosCleanup = DesktopHandoffPlanner.BuildBcdCleanupCommands(
            "{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}", @"S:\EFI\Microsoft\Boot\BCD");
        Assert(biosCleanup.Count == 2 && biosCleanup[0].Contains("/store") && biosCleanup[0].Contains("/delete"),
            "BCD cleanup commands must include /store and /delete.");

        var patchCmd = DesktopHandoffPlanner.BuildWimlibBootPatchCommand(
            @"C:\Tools\wimlib-imagex.exe",
            @"D:\FelixWinDeploy\pe\boot.wim",
            @"C:\Tools\felixpe-boot.cmd");
        Assert(patchCmd.Arguments.Any(a => a.Contains(@"\Windows\System32\felixpe-boot.cmd")),
            "Wimlib boot patch command must target \\Windows\\System32\\felixpe-boot.cmd.");

        Assert(DesktopHandoffPlanner.ResolveSessionRelativePath(@"D:\FelixWinDeploy", @"..\evil.wim") is null
            && DesktopHandoffPlanner.ResolveSessionRelativePath(@"D:\FelixWinDeploy", @"source\install.wim") is not null,
            "Session relative path resolution must reject traversal and accept valid subpaths.");

        var session = new DesktopHandoffSession(
            SchemaVersion: 1,
            SessionId: Guid.NewGuid().ToString("N"),
            CreatedUtc: DateTime.UtcNow,
            Request: request,
            RelativeSourceImagePath: @"source\Win11.iso");
        var roundtrip = DesktopHandoffSession.FromJson(session.ToJson());
        Assert(roundtrip.SessionId == session.SessionId
            && roundtrip.SchemaVersion == 1
            && roundtrip.RelativeSourceImagePath == @"source\Win11.iso",
            "DesktopHandoffSession JSON roundtrip failed.");

        var desktopDisk = new TargetDiskFacts(0, "id0", "ser0", "NVMe", "NVMe", "GPT", 256L << 30,
            false, true, false, false, false, false,
            [
                new(1, 1024 * 1024, 260L << 20, "", "EFI", "FAT32", "g1", "s1", true, false, false, false, false, false, false),
                new(2, 1L << 30, 100L << 30, "C", "Windows", "NTFS", "g2", "s2", false, false, false, false, false, false, true, 50L << 30),
                new(3, 102L << 30, 100L << 30, "D", "Data", "NTFS", "g3", "s3", false, false, false, false, false, false, false, 80L << 30)
            ]);
        var setupLetters = new HashSet<char>(['D']);
        Assert(TargetRules.EvaluateForMode(desktopDisk, setupLetters, isWinPe: false, InstallTargetMode.ExistingPartition, allowDesktopHandoff: true) == DiskBlockReason.None,
            "Desktop handoff in ExistingPartition mode must unlock physical disk holding running Windows when D: holds ISO.");
        Assert(TargetRules.EvaluateForMode(desktopDisk, setupLetters, isWinPe: false, InstallTargetMode.EraseDisk, allowDesktopHandoff: true) == DiskBlockReason.RunningWindows,
            "Desktop handoff in EraseDisk mode must still block disk holding running Windows.");
        Assert(!TargetRules.CanTakeRole(desktopDisk, desktopDisk.Partitions[2], PartitionRole.Windows, FirmwareMode.UefiGpt, allowDesktopHandoff: true, setupFileLetters: setupLetters),
            "Partition D: holding ISO must not be selectable as Windows target.");
    }

    private static void TestPhase1SourceBloatwareAndDrivers()
    {
        var withSize = new WindowsImageEdition(6, "Windows 11 Pro", "x64", "10.0.26100", 1742, 24_912_345_678L);
        Assert(withSize.DisplayName.Contains("23.2 GB", StringComparison.Ordinal),
            "Edition DisplayName must show size in GB when TotalBytes is set.");
        var withoutSize = new WindowsImageEdition(6, "Windows 11 Pro", "x64", "10.0.26100", 1742);
        Assert(!withoutSize.DisplayName.Contains("GB", StringComparison.Ordinal),
            "Edition DisplayName must omit size when TotalBytes is null.");

        var parsedDetails = DismWimInfoParser.ParseIndexDetails(
            "Architecture : x64\r\nVersion : 10.0.26100\r\nServicePack Build : 1742\r\nTotal Bytes : 24,912,345,678\r\n");
        Assert(parsedDetails.TotalBytes == 24_912_345_678L,
            "ParseIndexDetails must parse formatted Total Bytes count.");

        var apps = BloatwareCatalog.ParseWimWindowsAppsPaths(
            "\\Program Files\\WindowsApps\\Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe\r\n" +
            "\\Program Files\\WindowsApps\\Microsoft.WindowsStore_22405.1401.0.0_x64__8wekyb3d8bbwe\r\n" +
            "\\Program Files\\WindowsApps\\Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe\r\n");
        Assert(apps.SequenceEqual(["Microsoft.BingNews"]),
            "ParseWimWindowsAppsPaths must include BingNews and exclude protected Store/VCLibs.");

        var matched = BloatwareCatalog.ParseDismProvisionedPackages(
            "PackageName : Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe\r\n" +
            "PackageName : Microsoft.WindowsStore_22405.1401.0.0_neutral_~_8wekyb3d8bbwe\r\n",
            ["Microsoft.BingNews", "Microsoft.WindowsStore"]);
        Assert(matched.SequenceEqual(["Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe"]),
            "ParseDismProvisionedPackages must match selected bloatware and never remove Windows Store.");

        var missingStorage = DriverSelectionPolicy.FindUnselectedCriticalStorage(
            [
                new InstalledDriverEntry("oem1.inf", "iaStorVD.inf", false, "SCSIAdapter", "Intel", "2025-03-10", "20.1.0.10"),
                new InstalledDriverEntry("oem2.inf", "nv_dispig.inf", false, "Display", "NVIDIA", "2025-04-01", "32.0.15.0")
            ],
            new HashSet<string>(["oem2.inf"], StringComparer.OrdinalIgnoreCase));
        Assert(missingStorage.Count == 1 && missingStorage[0].InfFileName == "iaStorVD.inf",
            "FindUnselectedCriticalStorage must report unchecked storage driver.");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"sha-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var iso = Path.Combine(tmpDir, "test.iso");
            File.WriteAllText(iso, "felix-test-payload");
            var computed = SourceIntegrityChecker.ComputeSha256Async(iso).GetAwaiter().GetResult();
            File.WriteAllText(iso + ".sha256", $"{computed}  test.iso");
            var sidecar = SourceIntegrityChecker.TryReadSidecarSha256(iso);
            Assert(string.Equals(sidecar, computed, StringComparison.OrdinalIgnoreCase),
                "Sidecar SHA-256 must match computed file SHA-256.");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    private static async Task TestPhase2FelixWorkRescueUsbAndRecordingAsync()
    {
        var singlePartC = new TargetDiskFacts(
            0, "id0", "ser0", "NVMe", "NVMe", "GPT", 256L << 30,
            false, true, false, false, false, false,
            [
                new(1, 1024 * 1024, 260L << 20, "", "EFI", "FAT32", "g1", "s1", true, false, false, false, false, false, false),
                new(2, 1L << 30, 120L << 30, "C", "Windows", "NTFS", "g2", "s2", false, false, false, false, false, false, true, 80L << 30)
            ]);
        var shrinkDecision = DesktopHandoffPlanner.PlanStagingOrShrink(
            [new StagingVolumeCandidate('C', 80L << 30, "NTFS", false, false, true, true, true)],
            singlePartC,
            singlePartC.Partitions[1],
            InstallTargetMode.ExistingPartition,
            2L << 30);
        Assert(shrinkDecision is { Succeeded: true, RequiresFelixWorkShrink: true, ShrinkDiskNumber: 0, ShrinkPartitionNumber: 2 }
            && shrinkDecision.ShrinkSizeMiB >= 2048,
            "PlanStagingOrShrink must fall back to FELIXWORK shrink on single C: partition when free space suffices.");

        var mbrFull = singlePartC with
        {
            Style = "MBR",
            Partitions =
            [
                singlePartC.Partitions[0],
                singlePartC.Partitions[1],
                new(3, 130L << 30, 10L << 30, "D", "D", "NTFS", "", "", false, false, false, false, false, false, false, 1L << 30),
                new(4, 150L << 30, 10L << 30, "", "Recovery", "NTFS", "", "", false, false, true, false, false, false, false)
            ]
        };
        var mbrBlocked = DesktopHandoffPlanner.PlanStagingOrShrink(
            [new StagingVolumeCandidate('C', 80L << 30, "NTFS", false, false, true, true, true)],
            mbrFull,
            mbrFull.Partitions[1],
            InstallTargetMode.ExistingPartition,
            2L << 30);
        Assert(!mbrBlocked.Succeeded, "MBR disk with 4 primary partitions must block FELIXWORK shrink.");

        var workScript = InstallDiskLayoutBuilder.CreateFelixWorkShrinkScript(0, 2, 4096, 'K');
        Assert(workScript.Contains("label=FELIXWORK") && workScript.Contains("assign letter=K"),
            "FELIXWORK shrink script must create and label FELIXWORK.");
        var cleanupScript = InstallDiskLayoutBuilder.CreateRemoveFelixWorkAndExtendScript(0, 5, 2);
        Assert(cleanupScript.Contains("delete partition override") && cleanupScript.Contains("extend"),
            "Cleanup FELIXWORK script must delete temporary partition and extend Windows.");

        var usbFat32 = new TargetDiskFacts(3, "uid3", "user3", "SanDisk Ultra", "USB", "GPT", 32L << 30,
            false, false, false, false, false, false,
            [new(1, 1024 * 1024, 30L << 30, "U", "USB", "FAT32", "u1", "us1", false, false, false, false, false, false, false, 20L << 30)]);
        var evalKeep = RescueUsbPlanner.EvaluateUsbDisk(usbFat32, new HashSet<char>(['C']));
        Assert(evalKeep is { IsEligible: true, PreparationMode: RescueUsbMode.NonDestructiveFat32, ExistingFat32Letter: 'U' },
            "Clean FAT32 USB with >= 1.2GB free must use non-destructive mode.");

        var bigUsbHdd = new TargetDiskFacts(4, "uid4", "user4", "WD External", "USB", "GPT", 256L << 30,
            false, false, false, false, false, false,
            [new(1, 1024 * 1024, 240L << 30, "V", "BigUSB", "NTFS", "u2", "us2", false, false, false, false, false, false, false, 100L << 30)]);
        var evalFormat = RescueUsbPlanner.EvaluateUsbDisk(bigUsbHdd, new HashSet<char>(['C']));
        Assert(evalFormat is { IsEligible: true, PreparationMode: RescueUsbMode.FormatDisk, RequiresXoaConfirmation: true, ConfirmationToken: "XOA" },
            "Large NTFS USB disk >= 128 GB must require FormatDisk and XOA confirmation.");
        Assert(!RescueUsbPlanner.EvaluateUsbDisk(bigUsbHdd, new HashSet<char>(['V'])).IsEligible,
            "USB holding setup files must not be eligible for Rescue USB.");

        Assert(WinPeRecordingValidator.ValidateInterval(0, 30) is { IsValid: true, TotalSeconds: 30 }
            && !WinPeRecordingValidator.ValidateInterval(0, 5).IsValid
            && WinPeRecordingValidator.ValidateInterval(60, 0).IsValid
            && !WinPeRecordingValidator.ValidateInterval(61, 0).IsValid,
            "WinPeRecordingValidator must enforce 10s..60m bounds.");

        var stopRoot = Path.Combine(Path.GetTempPath(), $"safestop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stopRoot);
        try
        {
            var platform = new FakeInstallationPlatform();
            var control = new InstallExecutionControl
            {
                OnSafeStopBoundaryAsync = (_, writeStarted) =>
                {
                    Assert(!writeStarted, "Stopping before PrepareTarget must report writeStarted == false.");
                    return Task.FromResult(SafeStopDecision.AbortInstall);
                }
            };
            control.RequestSafeStop();
            var stopOutcome = await new WindowInstallationRunner(platform).RunAsync(
                CreateRequest(), stopRoot, null, CancellationToken.None, control);
            Assert(!stopOutcome.Succeeded && !stopOutcome.WriteStarted && !platform.PrepareCalled,
                "Safe stop before PrepareTarget must abort before formatting or writing.");
        }
        finally
        {
            Directory.Delete(stopRoot, true);
        }
    }

    private static void TestPhase3WindowsBackupAndHelp()
    {
        var capCmd = WimlibCommandBuilder.CreateCapture(
            "wimlib-imagex.exe",
            @"C:\",
            @"D:\Backups\Win11.esd",
            "Windows 11 Backup",
            "InPlace Snapshot",
            BackupArchiveFormat.Esd,
            snapshot: true,
            configFilePath: @"C:\temp\exclude.ini");
        Assert(capCmd.Arguments.SequenceEqual(
            ["capture", @"C:\", @"D:\Backups\Win11.esd", "Windows 11 Backup", "InPlace Snapshot", "--compress=LZMS", "--solid", "--snapshot", @"--config=C:\temp\exclude.ini"]),
            "WimlibCommandBuilder.CreateCapture must pass ESD solid compression, --snapshot, and --config flags.");

        var splitCmd = WimlibCommandBuilder.CreateSplit("wimlib-imagex.exe", @"D:\tmp.wim", @"D:\Backups\Win11.swm", 3800);
        Assert(splitCmd.Arguments.SequenceEqual(["split", @"D:\tmp.wim", @"D:\Backups\Win11.swm", "3800"]),
            "WimlibCommandBuilder.CreateSplit must produce expected wimlib split arguments.");

        Assert(WindowsBackupMetadata.IsInPlaceBackupEdition("Windows 11 Pro (Felix Backup - InPlace)")
            && !WindowsBackupMetadata.IsInPlaceBackupEdition("Windows 11 Pro (Felix Backup - Sysprep)"),
            "InPlace backup detection must distinguish InPlace backups from Sysprep images.");

        var liveSource = new BackupSourceInstallation(@"C:\", "Windows 11 Pro", "24H2 / 26100", IsCurrentRunningWindows: true, 0, 3, false);
        var rebootBlocked = WindowsBackupPreflightValidator.Validate(
            new WindowsBackupRequest(liveSource, WindowsBackupMethod.SysprepGeneralizeCapture, BackupArchiveFormat.Wim, @"D:\sysprep.wim", "Sysprep"),
            isRunningInWinPe: false,
            isRebootPending: true);
        Assert(!rebootBlocked.IsReady && rebootBlocked.Errors.Any(e => e.Contains("restart", StringComparison.OrdinalIgnoreCase)),
            "Pending reboot must block Method C Sysprep capture.");

        var insideWinBlocked = WindowsBackupPreflightValidator.Validate(
            new WindowsBackupRequest(liveSource, WindowsBackupMethod.LiveVssOrOfflineImage, BackupArchiveFormat.Wim, @"C:\Windows\Temp\bad.wim", "Bad"),
            isRunningInWinPe: false);
        Assert(!insideWinBlocked.IsReady, "Saving backup inside the source Windows directory must be blocked.");

        var validLive = WindowsBackupPreflightValidator.Validate(
            new WindowsBackupRequest(liveSource, WindowsBackupMethod.LiveVssOrOfflineImage, BackupArchiveFormat.Wim, @"D:\Win11.wim", "Good"),
            isRunningInWinPe: false);
        Assert(validLive.IsReady, "Valid backup request must pass preflight.");
        Assert(SourceDownloadCatalog.OfficialDownloadUrl == "https://www.felixplatforms.tech/windows",
            "OfficialDownloadUrl must point to https://www.felixplatforms.tech/windows.");

        const string sampleWimlibOutput = """
            Index:                  1
            Name:                   Windows Setup Boot
            Description:            Windows PE
            Display Name:           Microsoft Windows Setup (amd64)
            Architecture:           x86_64
            Installation Type:      WindowsPE
            Major Version:          10
            Minor Version:          0
            Build:                  26100
            Total Bytes:            456789123

            Index:                  2
            Name:                   Windows 11 Pro
            Description:            Windows 11 Pro
            Display Name:           Windows 11 Pro
            Architecture:           x86_64
            Installation Type:      Client
            Major Version:          10
            Minor Version:          0
            Build:                  26100
            Service Pack Build:     1742
            Total Bytes:            18234567890
            """;
        var parsedWim = WimlibInfoParser.Parse("sample.wim", sampleWimlibOutput);
        Assert(parsedWim.Editions.Count == 2
            && parsedWim.Editions[0].IsWindowsPe
            && !parsedWim.Editions[1].IsWindowsPe
            && parsedWim.Editions[1].Architecture == "x64"
            && parsedWim.Editions[1].Version == "10.0.26100"
            && parsedWim.Editions[1].ServicePackBuild == 1742,
            "WimlibInfoParser must distinguish WindowsPE from Windows OS editions and normalize x86_64 to x64.");

        const string peFlagsSample = """
            Index:                  1
            Name:                   Microsoft Windows PE (amd64)
            Description:            Microsoft Windows PE (amd64)
            Architecture:           x86_64
            Edition ID:             Enterprise
            Installation Type:      Client
            Major Version:          10
            Minor Version:          0
            Build:                  22631
            Flags:                  9
            Total Bytes:            2959123156
            """;
        var parsedFlagsPe = WimlibInfoParser.Parse("felixpe.wim", peFlagsSample);
        Assert(parsedFlagsPe.Editions.Count == 1
            && parsedFlagsPe.Editions[0].IsWindowsPe
            && SourceSearch.SkipDirectory(".tmp")
            && SourceSearch.IsExcludedImage(@"D:\stage\felixpe.wim", []),
            "WimlibInfoParser and SourceSearch must exclude Flags=9 WindowsPE images, .tmp directories, and felixpe.wim.");

        var modernReport = HardwareAdvisor.Evaluate(
            new HardwareSnapshot(
                "AMD Ryzen 7 7800X3D", 8, 16, 4200, "x64", new CpuCapabilities(true, true),
                32L << 30, "NVIDIA GeForce RTX 4070", 12L << 30, "B650", "F21",
                FirmwareMode.UefiGpt, true, true, "TPM 2.0",
                [new HardwareDiskSummary(0, "Samsung 990 Pro", "NVMe", true, 1000L << 30, "GPT")]),
            vietnamese: true);
        Assert(modernReport.OverallSeverity == HardwareCheckSeverity.Pass
            && modernReport.PrimaryEdition.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)
            && modernReport.Checks.Count == 6
            && modernReport.Options.Count == 4,
            "Modern UEFI + TPM 2.0 + NVMe hardware must score Pass and recommend Windows 11.");

        var legacyCpuReport = HardwareAdvisor.Evaluate(
            new HardwareSnapshot(
                "Intel Core 2 Quad Q6600", 4, 4, 2400, "x64", new CpuCapabilities(false, false),
                8L << 30, "Basic Display", null, "G31", "F1",
                FirmwareMode.BiosMbr, false, false, "",
                [new HardwareDiskSummary(0, "Kingston SATA", "SATA", true, 240L << 30, "MBR")]),
            vietnamese: false);
        Assert(legacyCpuReport.PrimaryEdition.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)
            && legacyCpuReport.Options.Any(o => o.Tier == EditionRecommendationTier.NotSupported),
            "CPU missing POPCNT/SSE4.2 must fall back to Windows 10 and mark Win 11 24H2 NotSupported.");

        var lowSpecHddReport = HardwareAdvisor.Evaluate(
            new HardwareSnapshot(
                "Intel Core i3-6100U", 2, 4, 2300, "x64", new CpuCapabilities(true, true),
                4L << 30, "Intel HD 520", 128L << 20, "Laptop", "1.0",
                FirmwareMode.UefiGpt, true, false, "",
                [new HardwareDiskSummary(0, "WDC 500GB", "SATA", false, 500L << 30, "GPT")]),
            vietnamese: true);
        Assert(lowSpecHddReport.PrimaryEdition.Contains("LTSC", StringComparison.OrdinalIgnoreCase),
            "Low RAM + HDD machine should prefer Windows LTSC edition.");
    }

    private static async Task TestInstallationRunnerAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"window-setup-runner-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var verifyRequest = CreateRequest() with { Options = new InstallOptions(VerifyImage: true) };
            var platform = new FakeInstallationPlatform();
            var outcome = await new WindowInstallationRunner(platform).RunAsync(
                verifyRequest,
                root);
            Assert(outcome.Succeeded, "The runner should complete when every platform stage succeeds.");
            Assert(platform.VerifyCalled && platform.PrepareCalled, "VerifySource and PrepareTarget should run when VerifyImage is true.");
            Assert(File.Exists(Path.Combine(outcome.RunDirectory, "journal.json")), "The runner should keep a journal.");
            Assert(File.Exists(Path.Combine(outcome.RunDirectory, "verify.log")), "The runner should keep verify evidence.");
            Assert(File.Exists(Path.Combine(outcome.RunDirectory, "verification.json")), "The runner should keep verification evidence.");
            Assert(platform.CleanupSucceeded, "A successful run should clean temporary access paths.");

            var verifyFailedPlatform = new FakeInstallationPlatform(verifyExitCode: 2);
            var verifyFailed = await new WindowInstallationRunner(verifyFailedPlatform).RunAsync(
                verifyRequest,
                root);
            Assert(!verifyFailed.Succeeded && !verifyFailed.WriteStarted && !verifyFailedPlatform.PrepareCalled,
                "A failed verification must abort before disk writes begin.");

            var noVerifyPlatform = new FakeInstallationPlatform();
            var noVerifyOutcome = await new WindowInstallationRunner(noVerifyPlatform).RunAsync(
                CreateRequest() with { Options = new InstallOptions(VerifyImage: false) },
                root);
            Assert(noVerifyOutcome.Succeeded && !noVerifyPlatform.VerifyCalled && noVerifyPlatform.PrepareCalled,
                "VerifyImage=false should skip VerifySource and continue installation.");

            var driverRequest = CreateRequest() with
            {
                Options = new InstallOptions(
                    Drivers: new DriverAndNetworkOptions(
                        BackupPreset: DriverBackupPreset.Minimum,
                        ExportConnectedWifi: true,
                        WindowsDriversFolder: root))
            };
            var driverPlan = InstallPlanBuilder.Create(driverRequest);
            var stageIdx = driverPlan.Steps.ToList().FindIndex(s => s.Kind == InstallPlanStepKind.StageDriversAndNetwork);
            var formatIdx = driverPlan.Steps.ToList().FindIndex(s => s.Kind == InstallPlanStepKind.FormatWindowsPartition);
            var applyIdx = driverPlan.Steps.ToList().FindIndex(s => s.Kind == InstallPlanStepKind.ApplyWindowsImage);
            var injectIdx = driverPlan.Steps.ToList().FindIndex(s => s.Kind == InstallPlanStepKind.InjectDriversAndNetwork);
            Assert(stageIdx >= 0 && stageIdx < formatIdx, "Install plan must stage drivers before formatting.");
            Assert(injectIdx > applyIdx, "Install plan must inject drivers after applying image.");

            var driverPlatform = new FakeInstallationPlatform();
            var driverOutcome = await new WindowInstallationRunner(driverPlatform).RunAsync(driverRequest, root);
            Assert(driverOutcome.Succeeded, "Runner must succeed when driver staging and injection succeed.");
            Assert(driverPlatform.CallOrder.IndexOf("Stage") >= 0
                && driverPlatform.CallOrder.IndexOf("Stage") < driverPlatform.CallOrder.IndexOf("Prepare"),
                "StageDriversAndNetworkAsync must run before PrepareTargetAsync.");
            Assert(driverPlatform.CallOrder.IndexOf("Inject") > driverPlatform.CallOrder.IndexOf("Apply"),
                "InjectDriversAndNetworkAsync must run after ApplyWindowsAsync.");

            var failed = await new WindowInstallationRunner(new FakeInstallationPlatform(engineExitCode: 9)).RunAsync(
                CreateRequest(),
                root);
            Assert(!failed.Succeeded && failed.WriteStarted, "An engine failure after preparation should remain a failed write run.");

            var shrinkRecoveryRequest = CreateRequest() with
            {
                Target = CreateRequest().Target with
                {
                    RecoveryPartition = new("none", "Do not use"),
                    DiskHasExistingRecovery = false
                },
                Options = new InstallOptions(
                    CreateRecovery: true,
                    BootAndRecovery: new BootAndRecoveryOptions(CreateNewRecovery: true))
            };
            var shrinkPlan = InstallPlanBuilder.Create(shrinkRecoveryRequest);
            Assert(shrinkPlan.Steps.Any(s => s.Kind == InstallPlanStepKind.CreateRecoveryPartition)
                && shrinkPlan.Steps.Any(s => s.Kind == InstallPlanStepKind.ConfigureRecovery),
                "ExistingPartition with no Recovery and CreateNewRecovery=true must plan Recovery creation and configuration.");
            var shrinkPlatform = new FakeInstallationPlatform();
            var shrinkOutcome = await new WindowInstallationRunner(shrinkPlatform).RunAsync(shrinkRecoveryRequest, root);
            Assert(shrinkOutcome.Succeeded && shrinkPlatform.ConfigureRecoveryCalled,
                "Runner must invoke ConfigureRecoveryAsync for shrink-to-create Recovery.");

            var firstBootOnlyReq = CreateRequest() with
            {
                Options = new InstallOptions(
                    FirstBoot: new FirstBootOptions(
                        RemoteApps: [new RemoteAppSelection(RemoteSupportAppKind.AnyDesk, true, "FLX123456789")],
                        WriteDesktopSummaryReport: true))
            };
            var firstBootPlan = InstallPlanBuilder.Create(firstBootOnlyReq);
            Assert(firstBootPlan.Steps.Any(s => s.Kind == InstallPlanStepKind.StageDriversAndNetwork)
                && firstBootPlan.Steps.Any(s => s.Kind == InstallPlanStepKind.InjectDriversAndNetwork),
                "Install plan must run pre-format staging and offline injection when FirstBoot options are active.");
            var firstBootPlatform = new FakeInstallationPlatform();
            var firstBootOutcome = await new WindowInstallationRunner(firstBootPlatform).RunAsync(firstBootOnlyReq, root);
            Assert(firstBootOutcome.Succeeded
                && firstBootPlatform.CallOrder.Contains("Stage")
                && firstBootPlatform.CallOrder.Contains("Inject"),
                "Runner must invoke StageDriversAndNetworkAsync and InjectDriversAndNetworkAsync for FirstBoot options.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TestDriverBackupAndWifi()
    {
        const string dismOutput = """
            Published Name : oem1.inf
            Original File Name : iaStorVD.inf
            Inbox : No
            Class Name : SCSIAdapter
            Provider Name : Intel
            Date : 03/10/2025
            Version : 20.1.0.1010

            Published Name : oem2.inf
            Original File Name : e1d68x64.inf
            Inbox : No
            Class Name : Net
            Provider Name : Intel
            Date : 01/05/2024
            Version : 12.19.1.0

            Published Name : oem3.inf
            Original File Name : e1d68x64.inf
            Inbox : No
            Class Name : Net
            Provider Name : Intel
            Date : 06/12/2025
            Version : 12.19.2.37

            Published Name : oem4.inf
            Original File Name : nv_dispig.inf
            Inbox : No
            Class Name : Display
            Provider Name : NVIDIA
            Date : 05/01/2025
            Version : 32.0.15.6094

            Published Name : oem5.inf
            Original File Name : realtek_hda.inf
            Inbox : No
            Class Name : MEDIA
            Provider Name : Realtek
            Date : 02/01/2025
            Version : 6.0.9700.1

            Published Name : netwtw14.inf
            Original File Name : netwtw14.inf
            Inbox : Yes
            Class Name : Net
            Provider Name : Intel
            Date : 01/01/2025
            Version : 23.0.0.1
            """;

        var parsed = DriverStoreParser.ParseDismGetDrivers(dismOutput, excludeInbox: true);
        Assert(parsed.Count == 5, "Inbox drivers must be excluded when excludeInbox is true.");

        var latest = DriverSelectionPolicy.KeepLatestOnly(parsed);
        Assert(latest.Count == 4 && latest.Any(e => e.PublishedName == "oem3.inf") && !latest.Any(e => e.PublishedName == "oem2.inf"),
            "KeepLatestOnly must retain the newer e1d68x64.inf driver version.");

        var minimum = DriverSelectionPolicy.SelectByPreset(parsed, DriverBackupPreset.Minimum);
        Assert(minimum.Count == 2 && minimum.Any(e => e.PublishedName == "oem1.inf") && minimum.Any(e => e.PublishedName == "oem3.inf"),
            "Minimum preset must include Storage/Intel VMD and latest Network only.");

        var recommended = DriverSelectionPolicy.SelectByPreset(parsed, DriverBackupPreset.Recommended);
        Assert(recommended.Count == 3 && !recommended.Any(e => e.PublishedName == "oem4.inf"),
            "Recommended preset must exclude Display drivers.");

        Assert(DriverSelectionPolicy.MatchesSearch(parsed[3], "nvidia")
            && !DriverSelectionPolicy.MatchesSearch(parsed[3], "realtek"),
            "Driver search must match provider case-insensitively.");

        const string netshOutput = """
            Name                   : Wi-Fi
            Description            : Intel(R) Wi-Fi 6E AX211
            State                  : connected
            SSID                   : FelixOffice5G
            BSSID                  : aa:bb:cc:dd:ee:ff
            """;
        Assert(WifiProfileParser.ParseConnectedSsid(netshOutput) == "FelixOffice5G",
            "WifiProfileParser must extract connected SSID and ignore BSSID.");
        Assert(WifiProfileParser.IsValidWifiProfileXml(
                "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>FelixOffice5G</name></WLANProfile>"),
            "Valid WLANProfile XML must validate.");
        Assert(!WifiProfileParser.IsValidWifiProfileXml("<Other />"),
            "Non-WLANProfile XML must fail validation.");

        var missingFolders = new InputFileInspection(true, true, true, null, null, false, false);
        var preflight = WindowSetupPreflight.Validate(CreateRequest() with { FileInspection = missingFolders });
        Assert(preflight.Issues.Any(i => i.Code == "drivers.windows-folder-not-found")
            && preflight.Issues.Any(i => i.Code == "drivers.winpe-folder-not-found"),
            "Missing manual driver folders must produce blocking preflight issues.");
    }

    private static void TestBootAndRecoveryPlanning()
    {
        Assert(RecoverySizeCalculator.CalculateRequiredMiB(0) == 1024,
            "Minimum recovery size must be 1024 MiB.");
        Assert(RecoverySizeCalculator.CalculateRequiredMiB(900L * 1024 * 1024) == 1200,
            "900 MiB WinRE with 300 MiB headroom must require 1200 MiB.");

        var gptShrink = InstallDiskLayoutBuilder.CreateShrinkRecoveryScript(2, 3, FirmwareMode.UefiGpt, 1200, 'R');
        Assert(gptShrink.Contains("shrink desired=1200 minimum=1200")
            && gptShrink.Contains("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")
            && gptShrink.Contains("assign letter=R"),
            "GPT shrink recovery script must shrink Windows and create hidden GPT recovery.");

        var mbrShrink = InstallDiskLayoutBuilder.CreateShrinkRecoveryScript(1, 2, FirmwareMode.BiosMbr, 1024, 'R');
        Assert(mbrShrink.Contains("set id=27 override"),
            "MBR shrink recovery script must set type 27.");

        var parallelArgs = ParallelWindowsPlanner.BuildBcdBootArguments(
            new ParallelWindowsEntry('E', @"E:\Windows", "Old Windows"),
            'S',
            FirmwareMode.UefiGpt);
        Assert(parallelArgs.SequenceEqual([@"E:\Windows", "/s", "S:", "/f", "UEFI", "/d"]),
            "Parallel Windows bcdboot args must include /d to keep new Windows default.");

        var (drive, rel) = ExtraWinPePlanner.ResolveRelativePath(@"D:\Rescue\boot.wim");
        Assert(drive == 'D' && rel == @"\Rescue\boot.wim", "Extra WinPE relative path resolution failed.");

        var guid = ExtraWinPePlanner.ParseCreatedBcdGuid(
            "The entry {12345678-1234-1234-1234-1234567890ab} was successfully created.");
        Assert(guid == "{12345678-1234-1234-1234-1234567890ab}", "BCD GUID parsing failed.");

        var uefiCmds = ExtraWinPePlanner.BuildRamdiskEntryCommands(@"S:\EFI\Microsoft\Boot\BCD", guid!, drive, rel, FirmwareMode.UefiGpt);
        Assert(uefiCmds.Any(c => c.Contains(@"ramdisk=[D:]\Rescue\boot.wim,{ramdiskoptions}"))
            && uefiCmds.Any(c => c.Contains(@"\Windows\system32\boot\winload.efi"))
            && uefiCmds.Any(c => c.SequenceEqual(["/store", @"S:\EFI\Microsoft\Boot\BCD", "/timeout", "10"])),
            "UEFI Extra WinPE BCD commands missing expected ramdisk/winload.efi/timeout.");

        var biosCmds = ExtraWinPePlanner.BuildRamdiskEntryCommands(@"S:\Boot\BCD", guid!, drive, rel, FirmwareMode.BiosMbr);
        Assert(biosCmds.Any(c => c.Contains(@"\Windows\system32\boot\winload.exe")),
            "BIOS Extra WinPE must target winload.exe.");

        Assert(WinPeImageClassifier.LooksLikeWinPe("Name : Microsoft Windows PE (amd64)", "boot.wim")
            && !WinPeImageClassifier.LooksLikeWinPe("Name : Windows 11 Pro", "install.wim"),
            "WinPeImageClassifier must distinguish WinPE WIM from regular Windows install.wim.");

        var dupRecoveryReq = CreateRequest() with
        {
            Target = CreateRequest().Target with
            {
                RecoveryPartition = new("none", "Do not use"),
                DiskHasExistingRecovery = true
            },
            Options = new InstallOptions(
                CreateRecovery: true,
                BootAndRecovery: new BootAndRecoveryOptions(CreateNewRecovery: true))
        };
        var dupPreflight = WindowSetupPreflight.Validate(dupRecoveryReq);
        Assert(dupPreflight.Issues.Any(i => i.Code == "recovery.duplicate-on-disk"),
            "Disk with existing Recovery must block creating a duplicate Recovery partition.");

        var badExtraReq = CreateRequest() with
        {
            Options = new InstallOptions(BootAndRecovery: new BootAndRecoveryOptions(ExtraWinPePath: @"E:\pe.iso")),
            FileInspection = new InputFileInspection(true, true, true, null, null, true, true, false, true, true, true)
        };
        var badExtraPreflight = WindowSetupPreflight.Validate(badExtraReq);
        Assert(badExtraPreflight.Issues.Any(i => i.Code == "extra-winpe.extension")
            && badExtraPreflight.Issues.Any(i => i.Code == "extra-winpe.not-found")
            && badExtraPreflight.Issues.Any(i => i.Code == "extra-winpe.on-target")
            && badExtraPreflight.Issues.Any(i => i.Code == "extra-winpe.removable")
            && badExtraPreflight.Issues.Any(i => i.Code == "extra-winpe.bitlocker"),
            "Extra WinPE validation must block invalid extension, missing file, target partition, USB, and BitLocker.");
    }

    private static void TestFirstBootPlanning()
    {
        var generated = RemotePasswordPolicy.Generate();
        Assert(generated.StartsWith("FLX", StringComparison.Ordinal)
            && generated.Length == 12
            && RemotePasswordPolicy.Validate(generated).IsValid,
            "Generated remote password must match FLX + 9 digits and pass validation.");

        Assert(!RemotePasswordPolicy.Validate("short").IsValid, "Short password must fail.");
        Assert(!RemotePasswordPolicy.Validate("has space1").IsValid, "Password with space must fail.");
        Assert(!RemotePasswordPolicy.Validate("12345678").IsValid, "Digit-only password without letter must fail.");
        Assert(!RemotePasswordPolicy.Validate("bad/slash1").IsValid, "Unsupported slash char must fail.");
        Assert(RemotePasswordPolicy.Validate("ValidPass!1").IsValid, "Valid custom password must pass.");

        var tempDir = Path.Combine(Path.GetTempPath(), $"firstboot-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, "bundle.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var readme = archive.CreateEntry("readme.txt");
                using (var w = new StreamWriter(readme.Open())) w.Write("info");
                var tool = archive.CreateEntry("Tools/setup.exe");
                using (var w = new StreamWriter(tool.Open())) w.Write("mz");
            }

            var exes = ZipProgramInspector.ListExecutables(zipPath);
            Assert(exes.SequenceEqual(["Tools/setup.exe"]), "ZipProgramInspector must list .exe entries inside ZIP.");

            var inspectValid = WindowSetupInputFiles.Inspect(zipPath, null, firstDesktopApp:
                new FirstDesktopAppOptions(FirstDesktopAppMode.ZipArchive, zipPath, "Tools/setup.exe"));
            Assert(inspectValid.FirstDesktopSourceExists && inspectValid.FirstDesktopZipProgramExists,
                "Valid ZIP + program inside ZIP must pass inspection.");

            var inspectMissingExe = WindowSetupInputFiles.Inspect(zipPath, null, firstDesktopApp:
                new FirstDesktopAppOptions(FirstDesktopAppMode.ZipArchive, zipPath, "missing.exe"));
            Assert(!inspectMissingExe.FirstDesktopZipProgramExists,
                "Missing executable inside ZIP must fail inspection.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }

        var script = FirstBootScriptBuilder.BuildSetupCompleteCmd(new FirstBootScriptSpec(
            HasDriversRetry: true,
            HasManualDriversRetry: false,
            HasWifiProfiles: true,
            HasPrinterBackup: true,
            EnableWinRe: true,
            SetBootTimeoutTenSeconds: true,
            EnabledRemoteApps:
            [
                new RemoteAppSelection(RemoteSupportAppKind.AnyDesk, true, "FLX998877665"),
                new RemoteAppSelection(RemoteSupportAppKind.RustDesk, true, "FLX112233445")
            ],
            DesktopLauncherPath: @"C:\ProgramData\FelixWinDeploy\FirstBoot\firstboot-desktop.cmd",
            WriteDesktopSummaryReport: true));

        Assert(script.Contains(@"pnputil.exe /add-driver ""%ProgramData%\FelixWinDeploy\FirstBoot\Drivers\*.inf"" /subdirs /install")
            && script.Contains("netsh.exe wlan add profile")
            && script.Contains("PrintBrm.exe\" -R -F")
            && script.Contains("AnyDesk.exe\" --set-password")
            && script.Contains("rustdesk.exe\" --password \"FLX112233445\"")
            && script.Contains("HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce")
            && script.Contains("reagentc.exe /enable"),
            "SetupComplete.cmd must contain drivers retry, Wi-Fi, printers, remote passwords, RunOnce, and WinRE.");

        var launcher = FirstBootScriptBuilder.BuildDesktopLauncherCmd([@"C:\Felix\Tools\app\tool.exe"]);
        Assert(launcher.Contains(@"if exist ""C:\Felix\Tools\app\tool.exe"" start """" ""C:\Felix\Tools\app\tool.exe"""),
            "Desktop launcher must guard and launch configured program.");

        var report = InstallSummaryReportBuilder.Build(
            CreateRequest() with
            {
                Options = new InstallOptions(FirstBoot: new FirstBootOptions(
                    RemoteApps:
                    [
                        new RemoteAppSelection(RemoteSupportAppKind.AnyDesk, true, "FLX998877665"),
                        new RemoteAppSelection(RemoteSupportAppKind.TeamViewerPortable, true)
                    ],
                    FirstDesktopApp: new FirstDesktopAppOptions(FirstDesktopAppMode.PortableExe, @"D:\Tools\app.exe")))
            },
            new StagedDriverArtifacts(@"C:\Drivers", @"C:\wifi.xml", null, 3));
        Assert(report.Contains("Backed up drivers: 3")
            && report.Contains("AnyDesk | Password: FLX998877665")
            && report.Contains("TeamViewer Portable | ID and session password appear in its desktop window")
            && report.Contains(@"At first desktop: D:\Tools\app.exe"),
            "Summary report must include drivers, remote passwords, TeamViewer note, and desktop app.");

        var badFirstBoot = WindowSetupPreflight.Validate(CreateRequest() with
        {
            Options = new InstallOptions(FirstBoot: new FirstBootOptions(
                RemoteApps: [new RemoteAppSelection(RemoteSupportAppKind.UltraViewer, true, "bad")],
                FirstDesktopApp: new FirstDesktopAppOptions(FirstDesktopAppMode.ZipArchive, @"D:\x.exe", ""))),
            FileInspection = new InputFileInspection(true, true, true, null, null, true, true, true, false, false, false, false, false)
        });
        Assert(badFirstBoot.Issues.Any(i => i.Code == "remote-app.invalid-password")
            && badFirstBoot.Issues.Any(i => i.Code == "first-desktop.extension")
            && badFirstBoot.Issues.Any(i => i.Code == "first-desktop.not-found")
            && badFirstBoot.Issues.Any(i => i.Code == "first-desktop.zip-program-missing")
            && badFirstBoot.Issues.Any(i => i.Code == "first-desktop.zip-program-not-found"),
            "First Boot preflight must block invalid password, wrong extension, missing file, and missing ZIP program.");
    }

    private static WindowSetupRequest CreateRequest() =>
        new(
            new("F:\\sources\\install.wim", 6, "Windows 11 Pro", "x64", "10.0.26100"),
            new(
                InstallTargetMode.ExistingPartition,
                "disk-0-samsung-s6wr-2f1",
                FirmwareMode.UefiGpt,
                new(
                    "partition-c",
                    "C: Windows",
                    Number: 3,
                    Offset: 300L * 1024 * 1024,
                    Size: 80L * 1024 * 1024 * 1024,
                    Guid: "windows-guid",
                    VolumeSerial: "windows-serial",
                    FileSystem: "NTFS"),
                new(
                    "partition-esp",
                    "EFI/System",
                    Number: 1,
                    Offset: 1024 * 1024,
                    Size: 260L * 1024 * 1024,
                    Guid: "efi-guid",
                    VolumeSerial: "efi-serial",
                    FileSystem: "FAT32"),
                new(
                    "partition-recovery",
                    "Recovery",
                    Number: 4,
                    Offset: 81L * 1024 * 1024 * 1024,
                    Size: 1024L * 1024 * 1024,
                    Guid: "recovery-guid",
                    VolumeSerial: "recovery-serial",
                    FileSystem: "NTFS"),
                DiskNumber: 0,
                DiskId: "disk-id",
                DiskSerial: "serial",
                DiskSize: 128L * 1024 * 1024 * 1024,
                DiskStyle: "GPT"),
            "F:\\Autounattend.xml");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeInstallationPlatform(int engineExitCode = 0, int verifyExitCode = 0) : IInstallationPlatform
    {
        public bool CleanupSucceeded { get; private set; }
        public bool VerifyCalled { get; private set; }
        public bool PrepareCalled { get; private set; }
        public bool ConfigureRecoveryCalled { get; private set; }
        public List<string> CallOrder { get; } = [];

        public Task<EngineExecutionResult> VerifySourceAsync(
            WindowSetupRequest request,
            string runDirectory,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            VerifyCalled = true;
            CallOrder.Add("Verify");
            return Task.FromResult(new EngineExecutionResult(verifyExitCode, "verify output"));
        }

        public Task<StagedDriverArtifacts?> StageDriversAndNetworkAsync(
            WindowSetupRequest request,
            string runDirectory,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallOrder.Add("Stage");
            return Task.FromResult<StagedDriverArtifacts?>(
                new StagedDriverArtifacts(Path.Combine(runDirectory, "Drivers"), null, null, 2));
        }

        public Task<PreparedInstallation> PrepareTargetAsync(
            WindowSetupRequest request,
            string runDirectory,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            PrepareCalled = true;
            CallOrder.Add("Prepare");
            var target = request.Target with
            {
                WindowsPartition = request.Target.WindowsPartition with { AccessPath = "W:" },
                EfiSystemPartition = request.Target.EfiSystemPartition with { AccessPath = "S:" },
                RecoveryPartition = request.Target.RecoveryPartition with { AccessPath = "R:" }
            };
            return Task.FromResult(new PreparedInstallation(
                request with { Target = target },
                runDirectory,
                ["S:", "W:", "R:"],
                []));
        }

        public Task<EngineExecutionResult> ApplyWindowsAsync(
            PreparedInstallation installation,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallOrder.Add("Apply");
            return Task.FromResult(new EngineExecutionResult(engineExitCode, "engine output"));
        }

        public Task InjectDriversAndNetworkAsync(
            PreparedInstallation installation,
            StagedDriverArtifacts? staged,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallOrder.Add("Inject");
            return Task.CompletedTask;
        }

        public Task ConfigureBootAsync(
            PreparedInstallation installation,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ConfigureRecoveryAsync(
            PreparedInstallation installation,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            ConfigureRecoveryCalled = true;
            return Task.CompletedTask;
        }

        public Task<InstallationVerificationResult> VerifyAsync(
            PreparedInstallation installation,
            IProgress<InstallProgressUpdate>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new InstallationVerificationResult(true, true, true, []));

        public Task CleanupAsync(PreparedInstallation installation, bool succeeded)
        {
            CleanupSucceeded = succeeded;
            return Task.CompletedTask;
        }
    }
}
