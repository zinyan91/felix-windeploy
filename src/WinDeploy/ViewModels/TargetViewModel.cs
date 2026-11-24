using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed record PartitionRoleOption(
    int DiskNumber,
    TargetPartitionFacts? Partition,
    string DisplayText);

public sealed class TargetViewModel : ObservableObject
{
    private readonly ITargetSource source;
    private readonly bool isWinPe;
    private readonly bool allowDesktopHandoff;
    private IReadOnlySet<char> setupLetters = new HashSet<char>();
    private InstallTargetMode mode;
    private FirmwareMode bootFirmware;
    private FirmwareMode eraseFirmware;
    private DiskRowViewModel? eraseDisk;
    private RoleSelection? roles;
    private TargetPlanSummary? summary;
    private PartitionCardViewModel? focusedCard;
    private PartitionRoleOption? selectedWindowsOption;
    private PartitionRoleOption? selectedSystemOption;
    private PartitionRoleOption? selectedRecoveryOption;
    private bool syncingRoleOptions;

    public TargetViewModel(ITargetSource source, FirmwareMode bootFirmware, bool isWinPe, bool? allowDesktopHandoff = null)
    {
        this.source = source;
        this.bootFirmware = bootFirmware;
        this.isWinPe = isWinPe;
        this.allowDesktopHandoff = allowDesktopHandoff ?? !isWinPe;
        eraseFirmware = bootFirmware;
    }

    public bool AllowDesktopHandoff => allowDesktopHandoff;

    public InstallTargetMode Mode
    {
        get => mode;
        set
        {
            if (SetField(ref mode, value))
            {
                ClearSelection();
                ReevaluateDiskBlocks();
                Raise(nameof(IsExistingMode));
                Raise(nameof(IsEraseMode));
                Raise(nameof(SelectedDiskHasExistingRecovery));
                Raise(nameof(IsValid));
            }
        }
    }

    public bool IsExistingMode
    {
        get => Mode == InstallTargetMode.ExistingPartition;
        set
        {
            if (value)
            {
                Mode = InstallTargetMode.ExistingPartition;
            }
        }
    }

    public bool IsEraseMode
    {
        get => Mode == InstallTargetMode.EraseDisk;
        set
        {
            if (value)
            {
                Mode = InstallTargetMode.EraseDisk;
            }
        }
    }

    public FirmwareMode BootFirmware
    {
        get => bootFirmware;
        private set
        {
            if (SetField(ref bootFirmware, value))
            {
                Raise(nameof(BootFirmwareLabel));
            }
        }
    }

    public string BootFirmwareLabel => BootFirmware == FirmwareMode.UefiGpt ? "UEFI" : "BIOS";

    public FirmwareMode EraseFirmware
    {
        get => eraseFirmware;
        set
        {
            if (SetField(ref eraseFirmware, value))
            {
                Raise(nameof(IsUefiErase));
                Raise(nameof(IsBiosErase));
                if (Mode == InstallTargetMode.EraseDisk && EraseDisk is not null)
                {
                    Summary = TargetSummaries.ForEraseMode(EraseDisk.Facts, eraseFirmware);
                    EraseDisk.PlannedLayout = Summary.Lines;
                    Raise(nameof(Summary));
                    Raise(nameof(EraseWarningText));
                }
            }
        }
    }

    public bool IsUefiErase
    {
        get => EraseFirmware == FirmwareMode.UefiGpt;
        set
        {
            if (value)
            {
                EraseFirmware = FirmwareMode.UefiGpt;
            }
        }
    }

    public bool IsBiosErase
    {
        get => EraseFirmware == FirmwareMode.BiosMbr;
        set
        {
            if (value)
            {
                EraseFirmware = FirmwareMode.BiosMbr;
            }
        }
    }

    public ObservableCollection<DiskRowViewModel> Disks { get; } = [];
    public ObservableCollection<PartitionRoleOption> WindowsRoleOptions { get; } = [];
    public ObservableCollection<PartitionRoleOption> SystemRoleOptions { get; } = [];
    public ObservableCollection<PartitionRoleOption> RecoveryRoleOptions { get; } = [];

    public PartitionRoleOption? SelectedWindowsOption
    {
        get => selectedWindowsOption;
        set
        {
            if (SetField(ref selectedWindowsOption, value) && !syncingRoleOptions && value?.Partition is { } p)
            {
                var card = FindCard(value.DiskNumber, p);
                if (card is not null)
                {
                    SelectPartition(card);
                }
            }
        }
    }

    public PartitionRoleOption? SelectedSystemOption
    {
        get => selectedSystemOption;
        set
        {
            if (SetField(ref selectedSystemOption, value) && !syncingRoleOptions && value?.Partition is { } p)
            {
                var card = FindCard(value.DiskNumber, p);
                if (card is not null)
                {
                    AssignRole(card, PartitionRole.System);
                }
            }
        }
    }

    public PartitionRoleOption? SelectedRecoveryOption
    {
        get => selectedRecoveryOption;
        set
        {
            if (SetField(ref selectedRecoveryOption, value) && !syncingRoleOptions)
            {
                if (value?.Partition is null)
                {
                    SetRecoveryUnused();
                }
                else
                {
                    var card = FindCard(value.DiskNumber, value.Partition);
                    if (card is not null)
                    {
                        AssignRole(card, PartitionRole.Recovery);
                    }
                }
            }
        }
    }

    public DiskRowViewModel? EraseDisk
    {
        get => eraseDisk;
        private set => SetField(ref eraseDisk, value);
    }

    public RoleSelection? Roles
    {
        get => roles;
        private set
        {
            if (SetField(ref roles, value))
            {
                Raise(nameof(HasRoles));
                Raise(nameof(SelectedDiskHasExistingRecovery));
                Raise(nameof(IsValid));
            }
        }
    }

    public PartitionCardViewModel? FocusedCard
    {
        get => focusedCard;
        private set
        {
            if (SetField(ref focusedCard, value))
            {
                Raise(nameof(HasFocusedCard));
            }
        }
    }

    public bool HasFocusedCard => FocusedCard is not null && Roles is not null;
    public bool HasRoles => Roles is not null;
    public bool SelectedDiskHasExistingRecovery =>
        Mode == InstallTargetMode.ExistingPartition
        && FindSelectedDiskFacts()?.Partitions.Any(p => p.IsRecovery) == true;

    public TargetPlanSummary? Summary
    {
        get => summary;
        private set
        {
            if (SetField(ref summary, value))
            {
                Raise(nameof(HasSummary));
                Raise(nameof(KeptText));
                Raise(nameof(EraseWarningText));
            }
        }
    }

    public bool HasSummary => Summary is not null;

    public string KeptText => Summary is { Kept.Count: > 0 }
        ? string.Join(", ", Summary.Kept.Select(FormatPartitionName))
        : "—";

    public string EraseWarningText
    {
        get
        {
            if (Mode != InstallTargetMode.EraseDisk || EraseDisk is null || Summary is null)
            {
                return "";
            }

            var lostNames = Summary.Lost.Count > 0
                ? string.Join(", ", Summary.Lost.Select(FormatPartitionName))
                : "none";
            var bitlocker = Summary.LostBitLocker.Count > 0
                ? $" BitLocker: {string.Join(", ", Summary.LostBitLocker.Select(FormatPartitionName))}."
                : "";
            return $"Disk {EraseDisk.Facts.Number} ({EraseDisk.Facts.Model}, {FormatSize(EraseDisk.Facts.Size)}) will be erased. Lost partitions: {lostNames}.{bitlocker}";
        }
    }

    public bool IsValid => Mode == InstallTargetMode.EraseDisk
        ? EraseDisk is not null && TargetRules.CanErase(EraseDisk.Facts)
        : Roles is not null;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var previousDisk = EraseDisk?.Facts ?? FindSelectedDiskFacts();
        var previousRoles = Roles;
        var facts = await source.LoadAsync(cancellationToken);
        Disks.Clear();
        foreach (var disk in facts)
        {
            var block = TargetRules.EvaluateForMode(disk, setupLetters, isWinPe, Mode, allowDesktopHandoff);
            Disks.Add(new DiskRowViewModel(disk, block, BootFirmware));
        }

        if (Mode == InstallTargetMode.EraseDisk && previousDisk is not null)
        {
            var matched = Disks.FirstOrDefault(row => SameDisk(row.Facts, previousDisk));
            if (matched is not null && matched.IsEnabled && TargetRules.CanErase(matched.Facts))
            {
                SelectDiskForErase(matched);
            }
            else
            {
                ClearSelection();
            }
        }
        else if (Mode == InstallTargetMode.ExistingPartition && previousDisk is not null && previousRoles is not null)
        {
            var row = Disks.FirstOrDefault(item => SameDisk(item.Facts, previousDisk));
            var windows = row?.Cards.FirstOrDefault(card => SamePartition(card.Facts, previousRoles.Windows));
            if (row is not null && row.IsEnabled && windows is not null)
            {
                SelectPartition(windows);
            }
            else
            {
                ClearSelection();
            }
        }
        else
        {
            ClearSelection();
        }
        RebuildRoleOptions();
        Raise(nameof(IsValid));
    }

    public void TryAutoSelectDefaultTarget()
    {
        if (Mode != InstallTargetMode.ExistingPartition || Roles is not null)
        {
            return;
        }

        PartitionCardViewModel? preferred = null;
        PartitionCardViewModel? fallback = null;
        foreach (var row in Disks)
        {
            if (!row.IsEnabled || row.PartitionBlock != PartitionModeBlock.None)
            {
                continue;
            }

            foreach (var card in row.Cards)
            {
                if (!TargetRules.CanTakeRole(row.Facts, card.Facts, PartitionRole.Windows, BootFirmware, allowDesktopHandoff, setupLetters))
                {
                    continue;
                }

                if (card.Facts.HasWindows && card.Facts.Letter.Equals("C", StringComparison.OrdinalIgnoreCase))
                {
                    preferred = card;
                    break;
                }

                if (card.Facts.HasWindows)
                {
                    preferred ??= card;
                }
                else
                {
                    fallback ??= card;
                }
            }

            if (preferred is not null)
            {
                break;
            }
        }

        if ((preferred ?? fallback) is { } chosen)
        {
            SelectPartition(chosen);
        }
    }

    public void UpdateSetupFiles(IReadOnlySet<char> letters)
    {
        setupLetters = letters;
        ReevaluateDiskBlocks();
        if (EraseDisk is not null && !EraseDisk.IsEnabled)
        {
            ClearSelection();
        }
        if (Roles is not null)
        {
            var selectedRow = Disks.FirstOrDefault(row =>
                row.Cards.Any(card => SamePartition(card.Facts, Roles.Windows)));
            if (selectedRow is null || !selectedRow.IsEnabled)
            {
                ClearSelection();
            }
        }
        Raise(nameof(IsValid));
    }

    public void SelectPartition(PartitionCardViewModel card)
    {
        var row = Disks.FirstOrDefault(item => item.Cards.Contains(card));
        if (row is null || !row.IsEnabled || card.IsUnallocated || row.PartitionBlock != PartitionModeBlock.None)
        {
            return;
        }

        if (FocusedCard is not null)
        {
            FocusedCard.IsFocused = false;
        }

        if (Roles is not null &&
            row.Cards.Any(c => SamePartition(c.Facts, Roles.Windows)) &&
            !TargetRules.CanTakeRole(row.Facts, card.Facts, PartitionRole.Windows, BootFirmware, allowDesktopHandoff, setupLetters))
        {
            FocusedCard = card;
            card.IsFocused = true;
            return;
        }

        var suggested = PartitionRoles.Suggest(row.Facts, card.Facts, BootFirmware, allowDesktopHandoff, setupLetters);
        if (suggested is null)
        {
            ClearSelection();
            return;
        }

        ClearAllCards();
        Roles = suggested;
        FocusedCard = card;
        card.IsFocused = true;
        ApplyRoleBadges(row);
        Summary = TargetSummaries.ForPartitionMode(row.Facts, Roles);
        RebuildRoleOptions();
        Raise(nameof(IsValid));
    }

    public void AssignRole(PartitionCardViewModel card, PartitionRole role)
    {
        var row = Disks.FirstOrDefault(item => item.Cards.Contains(card));
        if (row is null || !row.IsEnabled || card.IsUnallocated)
        {
            return;
        }

        if (Roles is null || !row.Cards.Any(c => SamePartition(c.Facts, Roles.Windows)))
        {
            if (role == PartitionRole.Windows)
            {
                SelectPartition(card);
            }
            return;
        }

        Roles = PartitionRoles.Assign(Roles, row.Facts, card.Facts, role, BootFirmware, allowDesktopHandoff, setupLetters);
        ApplyRoleBadges(row);
        Summary = TargetSummaries.ForPartitionMode(row.Facts, Roles);
        RebuildRoleOptions();
        Raise(nameof(IsValid));
    }

    private void ReevaluateDiskBlocks()
    {
        foreach (var row in Disks)
        {
            row.SetBlock(TargetRules.EvaluateForMode(row.Facts, setupLetters, isWinPe, Mode, allowDesktopHandoff), BootFirmware);
        }
    }

    public void SetRecoveryUnused()
    {
        if (Roles is null)
        {
            return;
        }

        var row = Disks.FirstOrDefault(item => item.Cards.Any(card => SamePartition(card.Facts, Roles.Windows)));
        if (row is null)
        {
            return;
        }

        Roles = Roles with { Recovery = null };
        ApplyRoleBadges(row);
        Summary = TargetSummaries.ForPartitionMode(row.Facts, Roles);
        RebuildRoleOptions();
        Raise(nameof(IsValid));
    }

    public void SelectDiskForErase(DiskRowViewModel row)
    {
        if (!row.IsEnabled || !TargetRules.CanErase(row.Facts))
        {
            return;
        }

        foreach (var disk in Disks)
        {
            disk.PlannedLayout = null;
            disk.IsEraseSelected = false;
        }

        if (Mode != InstallTargetMode.EraseDisk)
        {
            Mode = InstallTargetMode.EraseDisk;
        }

        EraseDisk = row;
        row.IsEraseSelected = true;
        Summary = TargetSummaries.ForEraseMode(row.Facts, EraseFirmware);
        row.PlannedLayout = Summary.Lines;
        Raise(nameof(EraseWarningText));
        Raise(nameof(IsValid));
    }

    public InstallTarget ToInstallTarget()
    {
        if (Mode == InstallTargetMode.EraseDisk)
        {
            var disk = EraseDisk?.Facts ?? throw new InvalidOperationException("Choose an erase disk.");
            return new(Mode, disk.Id, EraseFirmware, new("", ""), new("", ""), new("", ""),
                disk.Number, disk.Id, disk.Serial, disk.Size, disk.Style, disk.HoldsPeMedia,
                disk.IsReadOnly, disk.IsOffline, disk.IsDynamic, disk.Model, disk.Bus);
        }

        var selectedDisk = Disks.FirstOrDefault(row => Roles is not null && row.Cards.Any(card => SamePartition(card.Facts, Roles.Windows)))
            ?? throw new InvalidOperationException("Choose a Windows partition.");
        var recovery = Roles!.Recovery is null ? new PartitionTarget("none", "Do not use") : ToTarget(Roles.Recovery);
        return new(Mode, selectedDisk.Facts.Id, BootFirmware, ToTarget(Roles.Windows), ToTarget(Roles.System), recovery,
            selectedDisk.Facts.Number, selectedDisk.Facts.Id, selectedDisk.Facts.Serial, selectedDisk.Facts.Size,
            selectedDisk.Facts.Style, selectedDisk.Facts.HoldsPeMedia, selectedDisk.Facts.IsReadOnly,
            selectedDisk.Facts.IsOffline, selectedDisk.Facts.IsDynamic, selectedDisk.Facts.Model, selectedDisk.Facts.Bus,
            SelectedDiskHasExistingRecovery);
    }

    private TargetDiskFacts? FindSelectedDiskFacts()
    {
        if (Roles is null)
        {
            return null;
        }
        return Disks.FirstOrDefault(row => row.Cards.Any(card => SamePartition(card.Facts, Roles.Windows)))?.Facts;
    }

    private void ApplyRoleBadges(DiskRowViewModel row)
    {
        foreach (var candidate in row.Cards)
        {
            candidate.Role = Roles is null ? null :
                SamePartition(candidate.Facts, Roles.Windows) ? PartitionRole.Windows :
                SamePartition(candidate.Facts, Roles.System) ? PartitionRole.System :
                Roles.Recovery is not null && SamePartition(candidate.Facts, Roles.Recovery) ? PartitionRole.Recovery : null;
            candidate.IsSelected = candidate.Role is not null;
        }
    }

    private void ClearAllCards()
    {
        foreach (var row in Disks)
        {
            row.PlannedLayout = null;
            row.IsEraseSelected = false;
            foreach (var card in row.Cards)
            {
                card.Role = null;
                card.IsSelected = false;
                card.IsFocused = false;
            }
        }
    }

    private void ClearSelection()
    {
        Roles = null;
        FocusedCard = null;
        Summary = null;
        EraseDisk = null;
        ClearAllCards();
        RebuildRoleOptions();
    }

    private PartitionCardViewModel? FindCard(int diskNumber, TargetPartitionFacts partition) =>
        Disks.FirstOrDefault(d => d.Facts.Number == diskNumber)
            ?.Cards.FirstOrDefault(c => SamePartition(c.Facts, partition));

    private void RebuildRoleOptions()
    {
        syncingRoleOptions = true;
        try
        {
            WindowsRoleOptions.Clear();
            SystemRoleOptions.Clear();
            RecoveryRoleOptions.Clear();

            var noRecovery = new PartitionRoleOption(-1, null, "Do not use");
            RecoveryRoleOptions.Add(noRecovery);

            var activeDisk = FindSelectedDiskFacts();
            foreach (var row in Disks)
            {
                if (!row.IsEnabled)
                {
                    continue;
                }

                var isSelectedDisk = activeDisk is null || SameDisk(row.Facts, activeDisk);
                foreach (var card in row.Cards)
                {
                    var part = card.Facts;
                    if (TargetRules.CanTakeRole(row.Facts, part, PartitionRole.Windows, BootFirmware, allowDesktopHandoff, setupLetters))
                    {
                        WindowsRoleOptions.Add(new PartitionRoleOption(row.Facts.Number, part, FormatRoleOption(row.Facts.Number, part)));
                    }

                    if (isSelectedDisk && TargetRules.CanTakeRole(row.Facts, part, PartitionRole.System, BootFirmware, allowDesktopHandoff, setupLetters))
                    {
                        SystemRoleOptions.Add(new PartitionRoleOption(row.Facts.Number, part, FormatRoleOption(row.Facts.Number, part)));
                    }

                    if (isSelectedDisk && TargetRules.CanTakeRole(row.Facts, part, PartitionRole.Recovery, BootFirmware, allowDesktopHandoff, setupLetters))
                    {
                        RecoveryRoleOptions.Add(new PartitionRoleOption(row.Facts.Number, part, FormatRoleOption(row.Facts.Number, part)));
                    }
                }
            }

            SelectedWindowsOption = Roles is null
                ? null
                : WindowsRoleOptions.FirstOrDefault(o => o.Partition is not null && SamePartition(o.Partition, Roles.Windows));
            SelectedSystemOption = Roles is null
                ? null
                : SystemRoleOptions.FirstOrDefault(o => o.Partition is not null && SamePartition(o.Partition, Roles.System));
            SelectedRecoveryOption = Roles?.Recovery is null
                ? noRecovery
                : RecoveryRoleOptions.FirstOrDefault(o => o.Partition is not null && SamePartition(o.Partition, Roles.Recovery)) ?? noRecovery;
        }
        finally
        {
            syncingRoleOptions = false;
        }
    }

    private static string FormatRoleOption(int diskNumber, TargetPartitionFacts partition)
    {
        var namePart = string.IsNullOrWhiteSpace(partition.Letter)
            ? (string.IsNullOrWhiteSpace(partition.Label) ? $"Part {partition.Number}" : partition.Label)
            : $"{partition.Letter}: {partition.Label}".Trim();
        var lockText = partition.IsBitLocker ? " (BitLocker)" : "";
        return $"Disk {diskNumber} P{partition.Number} · {namePart} · {FormatSize(partition.Size)} · {partition.FileSystem}{lockText}";
    }

    private static bool SameDisk(TargetDiskFacts left, TargetDiskFacts right) =>
        left.Number == right.Number && left.Id == right.Id && left.Serial == right.Serial &&
        left.Size == right.Size && left.Style == right.Style;

    private static bool SamePartition(TargetPartitionFacts left, TargetPartitionFacts right) =>
        left.Offset == right.Offset && left.Size == right.Size &&
        left.Guid == right.Guid && left.VolumeSerial == right.VolumeSerial;

    private static PartitionTarget ToTarget(TargetPartitionFacts partition) =>
        new($"{partition.Number}:{partition.Guid}", FormatPartitionName(partition), partition.Letter,
            partition.Number, partition.Offset, partition.Size, partition.Guid, partition.VolumeSerial,
            partition.FileSystem, partition.IsBitLocker);

    private static string FormatPartitionName(TargetPartitionFacts partition)
    {
        var prefix = string.IsNullOrWhiteSpace(partition.Letter) ? "" : $"{partition.Letter}: ";
        var name = !string.IsNullOrWhiteSpace(partition.Label)
            ? partition.Label
            : $"Partition {partition.Number}";
        return $"{prefix}{name}".Trim();
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / (1024d * 1024 * 1024):0.#} GB";
}
