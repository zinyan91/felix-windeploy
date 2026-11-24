using System.Windows;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.Views;

public partial class RemoteAppsDialog : Window
{
    public RemoteAppsDialog(RemoteAppsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public RemoteAppsViewModel ViewModel { get; }
    public bool Confirmed { get; private set; }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemoteAppItemViewModel item })
        {
            item.RegeneratePassword();
        }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemoteAppItemViewModel item } && !string.IsNullOrEmpty(item.Password))
        {
            Clipboard.SetText(item.Password);
        }
    }

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
