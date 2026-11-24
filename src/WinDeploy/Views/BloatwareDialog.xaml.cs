using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.Views;

public partial class BloatwareDialog : Window
{
    public sealed class AppRow(string packagePrefix, string displayName, bool isSelected, Action onChanged) : INotifyPropertyChanged
    {
        private bool selected = isSelected;

        public string PackagePrefix { get; } = packagePrefix;
        public string DisplayName { get; } = displayName;

        public bool IsSelected
        {
            get => selected;
            set
            {
                if (selected != value)
                {
                    selected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    onChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<AppRow> allRows = [];
    private readonly string? imagePath;
    private readonly int imageIndex;
    private readonly IBloatwareService bloatwareService;

    public BloatwareDialog(
        bool initialEnabled,
        bool initialBlockPromoted,
        IReadOnlyList<string> initialSelectedPrefixes,
        string? imagePath = null,
        int imageIndex = 0,
        IBloatwareService? bloatwareService = null)
    {
        this.imagePath = imagePath;
        this.imageIndex = imageIndex;
        this.bloatwareService = bloatwareService ?? new NativeBloatwareService();
        InitializeComponent();

        EnableBloatwareCheck.IsChecked = initialEnabled;
        BlockPromotedCheck.IsChecked = initialBlockPromoted;
        ScanImageButton.IsEnabled = !string.IsNullOrWhiteSpace(imagePath) && imageIndex > 0;

        var selectedSet = new HashSet<string>(initialSelectedPrefixes, StringComparer.OrdinalIgnoreCase);
        foreach (var app in BloatwareCatalog.DefaultApps)
        {
            allRows.Add(new AppRow(app.PackagePrefix, app.DisplayName, selectedSet.Contains(app.PackagePrefix), UpdateSummary));
        }

        foreach (var prefix in initialSelectedPrefixes)
        {
            if (allRows.All(r => !r.PackagePrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                allRows.Add(new AppRow(prefix, prefix, true, UpdateSummary));
            }
        }

        RefreshFilter();
        UpdateSummary();
    }

    public bool Confirmed { get; private set; }
    public bool ResultEnabled => EnableBloatwareCheck.IsChecked == true;
    public bool ResultBlockPromoted => BlockPromotedCheck.IsChecked == true;
    public IReadOnlyList<string> ResultSelectedPrefixes =>
        allRows.Where(r => r.IsSelected).Select(r => r.PackagePrefix).ToArray();

    private void RefreshFilter()
    {
        var query = SearchBox.Text.Trim();
        AppsList.ItemsSource = string.IsNullOrEmpty(query)
            ? allRows.ToArray()
            : allRows.Where(r =>
                r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.PackagePrefix.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void UpdateSummary() =>
        SelectedSummaryText.Text = $"{allRows.Count(r => r.IsSelected)} / {allRows.Count}";

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        var rec = new HashSet<string>(
            BloatwareCatalog.DefaultApps.Where(a => a.DefaultSelected).Select(a => a.PackagePrefix),
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in allRows)
        {
            row.IsSelected = rec.Contains(row.PackagePrefix);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in allRows)
        {
            row.IsSelected = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in allRows)
        {
            row.IsSelected = false;
        }
    }

    private async void ScanImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imageIndex <= 0)
        {
            return;
        }

        ScanImageButton.IsEnabled = false;
        ScanProgressBar.Visibility = Visibility.Visible;
        try
        {
            var found = await bloatwareService.ScanImageAppsAsync(imagePath, imageIndex);
            foreach (var prefix in found)
            {
                if (allRows.All(r => !r.PackagePrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var known = BloatwareCatalog.DefaultApps.FirstOrDefault(a =>
                        a.PackagePrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
                    allRows.Add(new AppRow(prefix, known?.DisplayName ?? prefix, false, UpdateSummary));
                }
            }
            RefreshFilter();
            UpdateSummary();
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            ScanImageButton.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
