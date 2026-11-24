using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed class DriverItemViewModel : ObservableObject
{
    private readonly Action onSelectionChanged;
    private bool isChecked;

    public DriverItemViewModel(InstalledDriverEntry entry, Action onSelectionChanged)
    {
        Entry = entry;
        this.onSelectionChanged = onSelectionChanged;
    }

    public InstalledDriverEntry Entry { get; }

    public string PublishedName => Entry.PublishedName;
    public string InfFileName => Entry.InfFileName;
    public string ClassName => Entry.ClassName;
    public string ProviderName => Entry.ProviderName;
    public string VersionText => Entry.VersionText;
    public string DateText => Entry.DateText;

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (SetField(ref isChecked, value))
            {
                onSelectionChanged();
            }
        }
    }

    internal void SetCheckedSilent(bool value)
    {
        if (isChecked != value)
        {
            isChecked = value;
            Raise(nameof(IsChecked));
        }
    }
}

public sealed class DriverSelectionViewModel : ObservableObject
{
    private readonly IDriverStoreService driverStore;
    private readonly List<DriverItemViewModel> allItems = [];
    private readonly HashSet<string> initialSelectedNames;
    private DriverBackupPreset activePreset;
    private bool keepLatestOnly;
    private bool backupPrinters;
    private readonly bool isPrintBrmAvailable;
    private string searchText = "";
    private bool isLoading;
    private string? loadError;

    public DriverSelectionViewModel(
        IDriverStoreService? driverStore = null,
        DriverBackupPreset initialPreset = DriverBackupPreset.None,
        IReadOnlyList<string>? initialSelectedNames = null,
        bool initialKeepLatestOnly = true,
        bool initialBackupPrinters = false,
        bool isPrintBrmAvailable = false)
    {
        this.driverStore = driverStore ?? new NativeDriverStoreService();
        this.activePreset = initialPreset;
        this.keepLatestOnly = initialKeepLatestOnly;
        this.backupPrinters = initialBackupPrinters && isPrintBrmAvailable;
        this.isPrintBrmAvailable = isPrintBrmAvailable;
        this.initialSelectedNames = new HashSet<string>(
            initialSelectedNames ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public bool BackupPrinters
    {
        get => backupPrinters;
        set => SetField(ref backupPrinters, value);
    }

    public bool IsPrintBrmAvailable => isPrintBrmAvailable;

    public string? MissingCriticalStorageWarning
    {
        get
        {
            if (allItems.Count == 0)
            {
                return null;
            }

            var selected = new HashSet<string>(
                allItems.Where(i => i.IsChecked).Select(i => i.PublishedName),
                StringComparer.OrdinalIgnoreCase);
            var missing = DriverSelectionPolicy.FindUnselectedCriticalStorage(
                allItems.Select(i => i.Entry),
                selected);
            return missing.Count == 0
                ? null
                : string.Join(", ", missing.Select(d => d.InfFileName));
        }
    }

    public bool HasMissingCriticalStorageWarning => !string.IsNullOrWhiteSpace(MissingCriticalStorageWarning);

    public ObservableCollection<DriverItemViewModel> FilteredDrivers { get; } = [];

    public bool KeepLatestOnly
    {
        get => keepLatestOnly;
        set
        {
            if (SetField(ref keepLatestOnly, value))
            {
                RebuildFiltered();
            }
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetField(ref searchText, value))
            {
                RebuildFiltered();
            }
        }
    }

    public DriverBackupPreset ActivePreset
    {
        get => activePreset;
        private set => SetField(ref activePreset, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }

    public string? LoadError
    {
        get => loadError;
        private set => SetField(ref loadError, value);
    }

    public int TotalCount => allItems.Count;
    public int SelectedCount => allItems.Count(item => item.IsChecked);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        LoadError = null;
        try
        {
            var entries = await driverStore.ListThirdPartyDriversAsync(cancellationToken);
            allItems.Clear();
            foreach (var entry in entries)
            {
                allItems.Add(new DriverItemViewModel(entry, OnItemSelectionChanged));
            }

            if (initialSelectedNames.Count > 0)
            {
                foreach (var item in allItems)
                {
                    item.SetCheckedSilent(initialSelectedNames.Contains(item.PublishedName));
                }
            }
            else if (ActivePreset != DriverBackupPreset.None)
            {
                ApplyPresetCore(ActivePreset);
            }

            RebuildFiltered();
            Raise(nameof(TotalCount));
            Raise(nameof(SelectedCount));
            Raise(nameof(MissingCriticalStorageWarning));
            Raise(nameof(HasMissingCriticalStorageWarning));
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplyPreset(DriverBackupPreset preset)
    {
        ActivePreset = preset;
        ApplyPresetCore(preset);
        Raise(nameof(SelectedCount));
        Raise(nameof(MissingCriticalStorageWarning));
        Raise(nameof(HasMissingCriticalStorageWarning));
    }

    public void SelectAll()
    {
        ActivePreset = DriverBackupPreset.All;
        ApplyPresetCore(DriverBackupPreset.All);
        Raise(nameof(SelectedCount));
        Raise(nameof(MissingCriticalStorageWarning));
        Raise(nameof(HasMissingCriticalStorageWarning));
    }

    public void ClearSelection()
    {
        ActivePreset = DriverBackupPreset.None;
        foreach (var item in allItems)
        {
            item.SetCheckedSilent(false);
        }

        Raise(nameof(SelectedCount));
        Raise(nameof(MissingCriticalStorageWarning));
        Raise(nameof(HasMissingCriticalStorageWarning));
    }

    public IReadOnlyList<string> GetSelectedPublishedNames() =>
        allItems.Where(item => item.IsChecked).Select(item => item.PublishedName).ToList();

    private void ApplyPresetCore(DriverBackupPreset preset)
    {
        var entries = allItems.Select(item => item.Entry);
        var chosen = DriverSelectionPolicy.SelectByPreset(entries, preset, KeepLatestOnly);
        var chosenSet = new HashSet<string>(
            chosen.Select(entry => entry.PublishedName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in allItems)
        {
            item.SetCheckedSilent(chosenSet.Contains(item.PublishedName));
        }
    }

    private void RebuildFiltered()
    {
        IEnumerable<InstalledDriverEntry> pool = allItems.Select(item => item.Entry);
        if (KeepLatestOnly)
        {
            pool = DriverSelectionPolicy.KeepLatestOnly(pool);
        }

        var visibleSet = new HashSet<string>(
            pool.Select(entry => entry.PublishedName),
            StringComparer.OrdinalIgnoreCase);

        FilteredDrivers.Clear();
        foreach (var item in allItems)
        {
            if (visibleSet.Contains(item.PublishedName)
                && DriverSelectionPolicy.MatchesSearch(item.Entry, SearchText))
            {
                FilteredDrivers.Add(item);
            }
        }
    }

    private void OnItemSelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(MissingCriticalStorageWarning));
        Raise(nameof(HasMissingCriticalStorageWarning));
    }
}
