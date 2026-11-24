using System.Windows;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.Views;

public partial class BitLockerUnlockDialog : Window
{
    private readonly char driveLetter;
    private readonly IBitLockerService service;

    public BitLockerUnlockDialog(char driveLetter, string volumeLabel, IBitLockerService? service = null)
    {
        this.driveLetter = char.ToUpperInvariant(driveLetter);
        this.service = service ?? new NativeBitLockerService();
        InitializeComponent();
        VolumeHeader.Text = $"{this.driveLetter}: {volumeLabel}".Trim();
    }

    public bool Unlocked { get; private set; }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var input = SecretBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        UnlockButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            var result = RecoveryKeyModeRadio.IsChecked == true
                ? await service.UnlockWithRecoveryKeyAsync(driveLetter, input)
                : await service.UnlockWithPasswordAsync(driveLetter, input);

            if (!result.Succeeded)
            {
                ErrorText.Text = result.Output;
                return;
            }

            Unlocked = true;
            Close();
        }
        finally
        {
            UnlockButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
