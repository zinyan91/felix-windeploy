namespace Felix.WindowSetup.Core;

public static class WindowSetupPreflight
{
    private static readonly string[] SupportedImageExtensions = [".wim", ".esd", ".iso", ".swm"];

    public static PreflightResult Validate(WindowSetupRequest request, CpuCapabilities? cpu = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<PreflightIssue>();
        ValidateSource(request.Source, cpu, issues);
        ValidateTarget(request.Target, issues);
        ValidateAnswerFile(request.AutounattendPath, issues);
        ValidateBootAndRecovery(request, issues);
        ValidateFirstBoot(request, issues);
        ValidateFiles(request.FileInspection, issues);
        return new PreflightResult(issues);
    }

    private static void ValidateSource(WindowsSource source, CpuCapabilities? cpu, ICollection<PreflightIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(source.ImagePath))
        {
            issues.Add(new("source.missing", "Choose a Windows image before continuing.", true));
            return;
        }

        var extension = Path.GetExtension(source.ImagePath);
        if (!SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new("source.unsupported", "The source must be an ISO, WIM, ESD, or SWM image.", true));
        }

        if (string.IsNullOrWhiteSpace(source.Edition))
        {
            issues.Add(new("source.edition", "Choose a Windows edition before continuing.", true));
        }

        if (source.ImageIndex <= 0)
        {
            issues.Add(new("source.index", "Choose a valid Windows image index before continuing.", true));
        }
        if (string.IsNullOrWhiteSpace(source.Architecture) ||
            source.Architecture is not ("x64" or "amd64"))
        {
            issues.Add(new("source.architecture", "Felix WinDeploy currently supports x64 Windows images.", true));
        }
        if (cpu is not null &&
            HardwareRequirements.RequiresPopcntAndSse42(source.Architecture, source.Version) &&
            (!cpu.HasPopcnt || !cpu.HasSse42))
        {
            issues.Add(new("source.cpu-unsupported",
                "Windows build 26100 or newer requires a CPU with POPCNT and SSE4.2.",
                true));
        }
    }

    private static void ValidateTarget(InstallTarget target, ICollection<PreflightIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(target.DiskIdentity))
        {
            issues.Add(new("target.disk", "Choose a target disk before continuing.", true));
        }
        if (target.DiskNumber < 0)
        {
            issues.Add(new("target.disk-number", "The selected disk has no stable disk number.", true));
        }
        if (target.DiskSize <= 0)
        {
            issues.Add(new("target.disk-size", "The selected disk size could not be verified.", true));
        }
        if (target.HoldsPeMedia)
        {
            issues.Add(new("target.pe-media", "The disk containing FelixPE cannot be used as the target.", true));
        }
        if (target.IsReadOnly || target.IsOffline || target.IsDynamic)
        {
            issues.Add(new("target.unsupported", "The selected disk is offline, read-only, or dynamic.", true));
        }
        if (target.Mode == InstallTargetMode.EraseDisk &&
            target.DiskSize < InstallDiskLayoutBuilder.MinimumDiskBytes)
        {
            issues.Add(new("target.capacity", "Erase disk requires at least 32 GB.", true));
        }

        if (target.Mode == InstallTargetMode.ExistingPartition)
        {
            ValidatePartition(target.WindowsPartition, "Windows partition", issues);
            ValidatePartition(target.EfiSystemPartition, "EFI/System partition", issues);
            var selected = new[]
                {
                    target.WindowsPartition.Identity,
                    target.EfiSystemPartition.Identity,
                    target.RecoveryPartition.Identity
                }
                .Where(identity =>
                    !string.IsNullOrWhiteSpace(identity) &&
                    !identity.Equals("none", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var biosSharedSystem = target.Firmware == FirmwareMode.BiosMbr &&
                target.EfiSystemPartition.Identity.Equals(target.WindowsPartition.Identity, StringComparison.OrdinalIgnoreCase);
            if (!biosSharedSystem && selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            {
                issues.Add(new("target.duplicate", "Windows, EFI/System, and Recovery must use different partitions.", true));
            }
            if (target.WindowsPartition.Size < 20L * 1024 * 1024 * 1024)
            {
                issues.Add(new("target.windows-capacity", "The Windows partition requires at least 20 GB.", true));
            }
            if (target.Firmware == FirmwareMode.UefiGpt &&
                (!target.DiskStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase) ||
                 !target.EfiSystemPartition.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new("target.uefi", "UEFI installation requires a GPT disk and a FAT32 EFI partition.", true));
            }
            if (target.Firmware == FirmwareMode.BiosMbr &&
                !target.DiskStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("target.bios", "BIOS installation requires an MBR disk.", true));
            }
            if (!biosSharedSystem && target.EfiSystemPartition.Size < 100L * 1024 * 1024)
            {
                issues.Add(new("target.system-capacity", "The EFI/System partition requires at least 100 MB.", true));
            }
            if (!string.Equals(target.RecoveryPartition.Identity, "none", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePartition(target.RecoveryPartition, "Recovery partition", issues);
                if (target.RecoveryPartition.Size < 750L * 1024 * 1024)
                {
                    issues.Add(new("target.recovery-capacity", "The Recovery partition requires at least 750 MB.", true));
                }
            }
        }
    }

    private static void ValidatePartition(
        PartitionTarget partition,
        string name,
        ICollection<PreflightIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(partition.Identity))
        {
            issues.Add(new($"target.{name.Replace("/", "-", StringComparison.Ordinal).Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}",
                $"Choose a {name} before continuing.",
                true));
        }
        if (partition.IsBitLocker)
        {
            issues.Add(new($"target.{name.Replace("/", "-", StringComparison.Ordinal).Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-bitlocker",
                $"{name} is protected by BitLocker.",
                true));
        }
    }

    private static void ValidateAnswerFile(string? path, ICollection<PreflightIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("answer-file.extension", "The answer file must be an XML file.", true));
        }
    }

    private static void ValidateFiles(
        InputFileInspection? inspection,
        ICollection<PreflightIssue> issues)
    {
        if (inspection is null)
        {
            return;
        }

        if (!inspection.ImageExists)
        {
            issues.Add(new("source.not-found", "The selected Windows image does not exist.", true));
        }

        if (inspection.SwmMissingParts is { Count: > 0 })
        {
            issues.Add(new(
                "source.swm-incomplete",
                $"The split Windows image is missing part(s): {string.Join(", ", inspection.SwmMissingParts)}.",
                true));
        }

        if (!inspection.AnswerFileExists)
        {
            issues.Add(new("answer-file.not-found", "The selected Autounattend.xml does not exist.", true));
        }
        else if (!inspection.AnswerFileIsValid)
        {
            issues.Add(new("answer-file.invalid",
                inspection.AnswerFileError ?? "The selected Autounattend.xml is invalid.",
                true));
        }

        if (!inspection.WindowsDriversFolderExists)
        {
            issues.Add(new("drivers.windows-folder-not-found",
                "The selected Windows drivers folder does not exist.",
                true));
        }

        if (!inspection.WinPeDriversFolderExists)
        {
            issues.Add(new("drivers.winpe-folder-not-found",
                "The selected WinPE drivers folder does not exist.",
                true));
        }

        if (!inspection.ExtraWinPeExists)
        {
            issues.Add(new("extra-winpe.not-found",
                "The selected Extra WinPE image does not exist.",
                true));
        }

        if (inspection.ExtraWinPeOnTarget)
        {
            issues.Add(new("extra-winpe.on-target",
                "Extra WinPE cannot reside on the target Windows partition.",
                true));
        }

        if (inspection.ExtraWinPeIsRemovable)
        {
            issues.Add(new("extra-winpe.removable",
                "Extra WinPE cannot be placed on a removable USB drive.",
                true));
        }

        if (inspection.ExtraWinPeIsBitLocker)
        {
            issues.Add(new("extra-winpe.bitlocker",
                "Extra WinPE cannot reside on a BitLocker-encrypted partition.",
                true));
        }

        if (!inspection.FirstDesktopSourceExists)
        {
            issues.Add(new("first-desktop.not-found",
                "The selected at-first-desktop program file does not exist.",
                true));
        }

        if (!inspection.FirstDesktopZipProgramExists)
        {
            issues.Add(new("first-desktop.zip-program-not-found",
                "The selected executable was not found inside the ZIP archive.",
                true));
        }
    }

    private static void ValidateFirstBoot(
        WindowSetupRequest request,
        ICollection<PreflightIssue> issues)
    {
        var firstBoot = request.Options?.FirstBoot;
        if (firstBoot is null)
        {
            return;
        }

        foreach (var app in (firstBoot.RemoteApps ?? []).Where(a => a.Enabled))
        {
            if (!RemoteSupportCatalog.RequiresPassword(app.Kind))
            {
                continue;
            }

            var (valid, err) = RemotePasswordPolicy.Validate(app.Password);
            if (!valid)
            {
                issues.Add(new("remote-app.invalid-password",
                    $"{RemoteSupportCatalog.DisplayName(app.Kind)}: {err}",
                    true));
            }
        }

        var desktopApp = firstBoot.FirstDesktopApp;
        if (desktopApp is null || desktopApp.Mode == FirstDesktopAppMode.None)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(desktopApp.SourcePath))
        {
            issues.Add(new("first-desktop.source-missing",
                "Choose a program or ZIP archive for at first desktop.",
                true));
            return;
        }

        var ext = Path.GetExtension(desktopApp.SourcePath.Trim());
        if (desktopApp.Mode == FirstDesktopAppMode.PortableExe
            && !string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("first-desktop.extension",
                "The portable application must be an .exe file.",
                true));
        }
        else if (desktopApp.Mode == FirstDesktopAppMode.ZipArchive)
        {
            if (!string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("first-desktop.extension",
                    "The archive must be a .zip file.",
                    true));
            }

            if (string.IsNullOrWhiteSpace(desktopApp.ProgramInsideZip))
            {
                issues.Add(new("first-desktop.zip-program-missing",
                    "Choose the executable to launch inside the ZIP archive.",
                    true));
            }
        }
    }

    private static void ValidateBootAndRecovery(
        WindowSetupRequest request,
        ICollection<PreflightIssue> issues)
    {
        var boot = request.Options?.BootAndRecovery;
        if (boot is null)
        {
            return;
        }

        if (request.Target.Mode == InstallTargetMode.ExistingPartition
            && string.Equals(request.Target.RecoveryPartition.Identity, "none", StringComparison.OrdinalIgnoreCase)
            && boot.CreateNewRecovery
            && request.Target.DiskHasExistingRecovery)
        {
            issues.Add(new("recovery.duplicate-on-disk",
                "The selected disk already has a Recovery partition. Select it or disable creating a new Recovery partition.",
                true));
        }

        if (!string.IsNullOrWhiteSpace(boot.ExtraWinPePath)
            && !ExtraWinPePlanner.IsSupportedWimExtension(boot.ExtraWinPePath))
        {
            issues.Add(new("extra-winpe.extension",
                "Extra WinPE must be a .wim image.",
                true));
        }
    }
}
