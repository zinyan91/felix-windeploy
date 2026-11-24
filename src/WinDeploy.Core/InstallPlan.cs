namespace Felix.WindowSetup.Core;

public enum InstallPlanStepKind
{
    VerifySource,
    StageDriversAndNetwork,
    EraseDisk,
    CreateEfiSystemPartition,
    CreateWindowsPartition,
    CreateRecoveryPartition,
    FormatWindowsPartition,
    PrepareEfiSystemPartition,
    PrepareRecoveryPartition,
    ApplyWindowsImage,
    InjectDriversAndNetwork,
    ConfigureBoot,
    ConfigureRecovery,
    VerifyInstallation
}

public sealed record InstallPlanStep(
    InstallPlanStepKind Kind,
    string Description,
    bool IsDestructive);

public sealed record InstallPlan(
    InstallTargetMode Mode,
    IReadOnlyList<InstallPlanStep> Steps)
{
    public bool IsDestructive => Steps.Any(step => step.IsDestructive);
}

public static class InstallPlanBuilder
{
    public static InstallPlan Create(WindowSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var drivers = request.Options?.Drivers;
        var bootAndRecovery = request.Options?.BootAndRecovery;
        var firstBoot = request.Options?.FirstBoot;
        var createShrinkRecovery = request.Target.Mode == InstallTargetMode.ExistingPartition
            && IsSkipped(request.Target.RecoveryPartition)
            && (request.Options ?? new()).CreateRecovery
            && bootAndRecovery?.CreateNewRecovery == true;
        var steps = new List<InstallPlanStep>
        {
            new(InstallPlanStepKind.VerifySource, $"Verify {request.Source.ImagePath}", false)
        };

        if (drivers?.HasBackupOrStagingWork == true || firstBoot?.HasPreFormatStagingWork == true)
        {
            steps.Add(new(InstallPlanStepKind.StageDriversAndNetwork,
                "Stage selected drivers, network, and First Boot payloads", false));
        }

        if (request.Target.Mode == InstallTargetMode.EraseDisk)
        {
            steps.Add(new(InstallPlanStepKind.EraseDisk,
                $"Erase Disk {request.Target.DiskNumber}", true));
            steps.Add(new(InstallPlanStepKind.CreateEfiSystemPartition,
                $"Create {request.Target.Firmware} EFI/System partition", true));
            steps.Add(new(InstallPlanStepKind.CreateWindowsPartition,
                "Create the Windows partition in remaining space", true));
            if ((request.Options ?? new()).CreateRecovery)
            {
                steps.Add(new(InstallPlanStepKind.CreateRecoveryPartition,
                    "Create the Recovery partition", true));
            }
        }
        else
        {
            var sharedSystem = request.Target.Firmware == FirmwareMode.BiosMbr &&
                request.Target.WindowsPartition.Identity.Equals(
                    request.Target.EfiSystemPartition.Identity,
                    StringComparison.OrdinalIgnoreCase);
            steps.Add(new(InstallPlanStepKind.FormatWindowsPartition,
                sharedSystem
                    ? $"Format {request.Target.WindowsPartition.DisplayName} and add boot files"
                    : $"Format {request.Target.WindowsPartition.DisplayName}", true));
            if (!sharedSystem)
            {
                steps.Add(new(InstallPlanStepKind.PrepareEfiSystemPartition,
                    $"Prepare {request.Target.EfiSystemPartition.DisplayName}", true));
            }
            if (!IsSkipped(request.Target.RecoveryPartition))
            {
                steps.Add(new(InstallPlanStepKind.PrepareRecoveryPartition,
                    $"Prepare {request.Target.RecoveryPartition.DisplayName}", true));
            }
            else if (createShrinkRecovery)
            {
                steps.Add(new(InstallPlanStepKind.CreateRecoveryPartition,
                    "Shrink Windows partition and create Recovery partition", true));
            }
        }

        steps.Add(new(InstallPlanStepKind.ApplyWindowsImage,
            $"Apply {request.Source.Edition}", true));
        if (drivers?.HasInjectionOrFirstBootWork == true || firstBoot?.HasOfflineWork == true)
        {
            steps.Add(new(InstallPlanStepKind.InjectDriversAndNetwork,
                "Inject drivers and stage First Boot payloads", true));
        }
        steps.Add(new(InstallPlanStepKind.ConfigureBoot,
            $"Configure {request.Target.Firmware} boot files", true));
        if ((request.Options ?? new()).CreateRecovery &&
            (request.Target.Mode == InstallTargetMode.EraseDisk ||
             !IsSkipped(request.Target.RecoveryPartition) ||
             createShrinkRecovery))
        {
            steps.Add(new(InstallPlanStepKind.ConfigureRecovery,
                "Configure Windows Recovery Environment", true));
        }
        steps.Add(new(InstallPlanStepKind.VerifyInstallation,
            "Verify Windows and boot files", false));

        return new InstallPlan(request.Target.Mode, steps);
    }

    private static bool IsSkipped(PartitionTarget partition) =>
        string.Equals(partition.Identity, "none", StringComparison.OrdinalIgnoreCase);
}
