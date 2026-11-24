using System.Windows;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.Views;

public partial class DriverDialog : Window
{
    public DriverDialog(DriverSelectionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += DriverDialog_Loaded;
    }

    public DriverSelectionViewModel ViewModel { get; }
    public bool Confirmed { get; private set; }

    private async void DriverDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.TotalCount == 0 && !ViewModel.IsLoading)
        {
            await ViewModel.LoadAsync();
        }
    }

    private void RecommendedPreset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyPreset(DriverBackupPreset.Recommended);

    private void MinimumPreset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyPreset(DriverBackupPreset.Minimum);

    private void AllPreset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SelectAll();

    private void ClearPreset_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ClearSelection();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
