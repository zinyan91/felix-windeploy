using System.Windows;
using System.Windows.Controls;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.Views;

public partial class MakeRescueUsbDialog : Window
{
    private readonly RescueUsbCandidate candidate;
    private readonly string? windowsSourcePath;
    private readonly IRescueUsbService service;

    public MakeRescueUsbDialog(
        RescueUsbCandidate candidate,
        string? windowsSourcePath = null,
        IRescueUsbService? service = null)
    {
        this.candidate = candidate;
        this.windowsSourcePath = windowsSourcePath;
        this.service = service ?? new NativeRescueUsbService();
        InitializeComponent();

        UsbSummaryText.Text = candidate.DisplayText;
        if (candidate.PreparationMode == RescueUsbMode.NonDestructiveFat32)
        {
            KeepDataText.Visibility = Visibility.Visible;
            EraseWarningBanner.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Collapsed;
            CreateUsbButton.IsEnabled = candidate.IsEligible;
        }
        else
        {
            KeepDataText.Visibility = Visibility.Collapsed;
            EraseWarningBanner.Visibility = Visibility.Visible;
            ConfirmPanel.Visibility = Visibility.Visible;
            var label = TryFindResource("WS_RescueUsb_TypeConfirm") as string ?? "Type confirmation token";
            ConfirmPromptText.Text = $"{label}: {candidate.ConfirmationToken}";
            CreateUsbButton.IsEnabled = false;
        }
    }

    public bool Succeeded { get; private set; }

    private void ConfirmBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (candidate.PreparationMode == RescueUsbMode.FormatDisk)
        {
            CreateUsbButton.IsEnabled = string.Equals(
                ConfirmBox.Text.Trim(),
                candidate.ConfirmationToken,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateUsbButton.IsEnabled = false;
        CloseUsbButton.IsEnabled = false;
        WorkProgressBar.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            await service.CreateBootRescueUsbAsync(candidate, windowsSourcePath, progress);
            Succeeded = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            WorkProgressBar.Visibility = Visibility.Collapsed;
            CloseUsbButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
