using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.ViewModels;

public sealed class PartitionCardViewModel(TargetPartitionFacts facts) : ObservableObject
{
    private PartitionRole? role;
    private bool isSelected;
    private bool isFocused;
    private bool canSelect;
    private bool canAssignWindows;
    private bool canAssignSystem;
    private bool canAssignRecovery;

    public TargetPartitionFacts Facts { get; } = facts;

    public PartitionRole? Role
    {
        get => role;
        internal set
        {
            if (SetField(ref role, value))
            {
                Raise(nameof(RoleBadge));
                Raise(nameof(HasRole));
            }
        }
    }

    public bool IsSelected { get => isSelected; internal set => SetField(ref isSelected, value); }
    public bool IsFocused { get => isFocused; internal set => SetField(ref isFocused, value); }
    public bool CanSelect { get => canSelect; internal set => SetField(ref canSelect, value); }
    public bool CanAssignWindows { get => canAssignWindows; internal set => SetField(ref canAssignWindows, value); }
    public bool CanAssignSystem { get => canAssignSystem; internal set => SetField(ref canAssignSystem, value); }
    public bool CanAssignRecovery { get => canAssignRecovery; internal set => SetField(ref canAssignRecovery, value); }

    public double Weight => Math.Max(1d, Facts.Size);
    public bool IsUnallocated => Facts.IsUnallocated;
    public bool IsBitLocker => Facts.IsBitLocker;
    public bool HasRole => Role is not null;
    public bool CanUnlockBitLocker => Facts.IsBitLocker && !string.IsNullOrWhiteSpace(Facts.Letter);
    public bool CanTurnOffBitLocker =>
        !Facts.IsUnallocated
        && !Facts.IsEfi
        && !Facts.IsRecovery
        && !string.IsNullOrWhiteSpace(Facts.Letter)
        && Facts.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => Facts.IsUnallocated
        ? "Unallocated"
        : string.IsNullOrWhiteSpace(Facts.Letter) && string.IsNullOrWhiteSpace(Facts.Label)
            ? $"Partition {Facts.Number}"
            : $"{(string.IsNullOrWhiteSpace(Facts.Letter) ? "" : Facts.Letter + ":")} {Facts.Label}".Trim();

    public string FileSystemText => Facts.IsUnallocated ? "" : Facts.FileSystem;

    public string SizeText => FormatBytes(Facts.Size);

    public double UsedRatio
    {
        get
        {
            if (Facts.IsUnallocated || Facts.Size <= 0 || Facts.FreeBytes <= 0 || Facts.FreeBytes > Facts.Size)
            {
                return 0d;
            }
            return Math.Clamp((double)(Facts.Size - Facts.FreeBytes) / Facts.Size, 0d, 1d);
        }
    }

    public string RoleBadge => Role switch
    {
        PartitionRole.Windows => "Windows",
        PartitionRole.System => Facts.IsEfi ? "EFI" : "System",
        PartitionRole.Recovery => "Recovery",
        _ => ""
    };

    private static string FormatBytes(long bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;
        return bytes >= gib ? $"{bytes / gib:0.#} GB" : $"{bytes / mib:0} MB";
    }
}
