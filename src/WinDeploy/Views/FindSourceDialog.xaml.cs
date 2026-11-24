using System.Windows;
using System.Windows.Input;
using Felix.UI.Controls;
using Felix.WindowSetup.Services;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.Views;

public partial class FindSourceDialog : FeatureWindow
{
    public FindSourceDialog()
        : this(new NativeSourceScanner(new NativeTargetSource()), new WindowsSourceInspector())
    {
    }

    public FindSourceDialog(ISourceScanner scanner, IImageInspector inspector)
    {
        ViewModel = new FindSourceViewModel(scanner, inspector);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += async (_, _) => await ViewModel.StartAsync();
        Closed += (_, _) => ViewModel.Cancel();
    }

    public FindSourceViewModel ViewModel { get; }
    public string? SelectedPath { get; private set; }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ViewModel.StartAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Cancel();
        Close();
    }

    private void Use_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.Selected is not null)
        {
            ConfirmSelection();
        }
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.Selected is not null)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private void ConfirmSelection()
    {
        SelectedPath = ViewModel.Selected?.Candidate.Path;
        if (!string.IsNullOrWhiteSpace(SelectedPath))
        {
            ViewModel.Cancel();
            Close();
        }
    }
}
