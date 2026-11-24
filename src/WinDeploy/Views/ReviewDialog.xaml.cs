using System.Windows;
using Felix.WindowSetup.ViewModels;

namespace Felix.WindowSetup.Views;

public partial class ReviewDialog : Window
{
    public sealed record DisplayRow(string Label, string Value);

    public ReviewDialog(ReviewViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        RowsList.ItemsSource = ViewModel.Rows
            .Select(row => new DisplayRow(TryFindResource(row.LabelKey) as string ?? row.LabelKey, row.Value))
            .ToArray();
    }

    public ReviewViewModel ViewModel { get; }
    public bool Confirmed { get; private set; }

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanConfirm)
        {
            return;
        }
        Confirmed = true;
        Close();
    }
}
