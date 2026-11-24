using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.ViewModels;

public sealed class DiskRowViewModel : ObservableObject
{
    private DiskBlockReason block;
    private PartitionModeBlock partitionBlock;
    private IReadOnlyList<SummaryLine>? plannedLayout;
    private bool isEraseSelected;

    public DiskRowViewModel(TargetDiskFacts facts, DiskBlockReason block, FirmwareMode firmware)
    {
        Facts = facts;
        this.block = block;
        partitionBlock = TargetRules.PartitionModeBlock(facts, firmware);
        Cards = new ObservableCollection<PartitionCardViewModel>(
            facts.Partitions.Select(partition => new PartitionCardViewModel(partition)));
        UpdateCardStates(firmware);
    }

    public TargetDiskFacts Facts { get; }

    public DiskBlockReason Block
    {
        get => block;
        private set
        {
            if (SetField(ref block, value))
            {
                Raise(nameof(IsEnabled));
                Raise(nameof(BlockReason));
                Raise(nameof(HasBlockReason));
                Raise(nameof(CanSelectForPartitionMode));
            }
        }
    }

    public PartitionModeBlock PartitionBlock
    {
        get => partitionBlock;
        private set
        {
            if (SetField(ref partitionBlock, value))
            {
                Raise(nameof(PartitionBlockReason));
                Raise(nameof(HasPartitionBlockReason));
                Raise(nameof(CanSelectForPartitionMode));
            }
        }
    }

    public ObservableCollection<PartitionCardViewModel> Cards { get; }

    public IReadOnlyList<SummaryLine>? PlannedLayout
    {
        get => plannedLayout;
        internal set
        {
            if (SetField(ref plannedLayout, value))
            {
                Raise(nameof(HasPlannedLayout));
            }
        }
    }

    public bool IsEraseSelected
    {
        get => isEraseSelected;
        internal set => SetField(ref isEraseSelected, value);
    }

    public bool HasPlannedLayout => PlannedLayout is { Count: > 0 };
    public bool IsEnabled => Block == DiskBlockReason.None;
    public bool CanSelectForPartitionMode => IsEnabled && PartitionBlock == PartitionModeBlock.None;
    public bool HasBlockReason => Block != DiskBlockReason.None;
    public bool HasPartitionBlockReason => IsEnabled && PartitionBlock != PartitionModeBlock.None;

    public string DisplayName => $"Disk {Facts.Number} · {Facts.Model} · {Facts.Bus} · {Facts.Style} · {FormatSize(Facts.Size)}";

    public string BlockReason => Block switch
    {
        DiskBlockReason.FelixPe => "FelixPE media",
        DiskBlockReason.RunningWindows => "Running Windows",
        DiskBlockReason.ReadOnly => "Read-only",
        DiskBlockReason.Offline => "Offline",
        DiskBlockReason.Dynamic => "Dynamic disk",
        DiskBlockReason.HoldsSetupFiles => "Contains setup files",
        DiskBlockReason.DesktopPhysicalDisk => "Only writable in FelixPE",
        _ => ""
    };

    public string PartitionBlockReason => PartitionBlock switch
    {
        PartitionModeBlock.StyleMismatch => $"Requires {(Facts.Style == "MBR" ? "GPT" : "MBR")} for current firmware",
        PartitionModeBlock.NoSystemPartition => "No compatible EFI/System partition",
        _ => ""
    };

    internal void SetBlock(DiskBlockReason value, FirmwareMode firmware)
    {
        Block = value;
        PartitionBlock = TargetRules.PartitionModeBlock(Facts, firmware);
        UpdateCardStates(firmware);
    }

    internal void UpdateCardStates(FirmwareMode firmware)
    {
        foreach (var card in Cards)
        {
            if (!IsEnabled || card.IsUnallocated)
            {
                card.CanSelect = false;
                card.CanAssignWindows = false;
                card.CanAssignSystem = false;
                card.CanAssignRecovery = false;
                continue;
            }

            card.CanAssignWindows = TargetRules.CanTakeRole(Facts, card.Facts, PartitionRole.Windows, firmware);
            card.CanAssignSystem = TargetRules.CanTakeRole(Facts, card.Facts, PartitionRole.System, firmware);
            card.CanAssignRecovery = TargetRules.CanTakeRole(Facts, card.Facts, PartitionRole.Recovery, firmware);
            card.CanSelect = CanSelectForPartitionMode &&
                (card.CanAssignWindows || card.CanAssignSystem || card.CanAssignRecovery);
        }
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / (1024d * 1024 * 1024):0.#} GB";
}
