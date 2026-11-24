using System.Windows;
using System.Windows.Controls;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Views;

public partial class RecordingDialog : Window
{
    private readonly bool ready;

    public RecordingDialog(WinPeRecordingOptions initialOptions)
    {
        InitializeComponent();
        var total = Math.Clamp(initialOptions.ScreenshotIntervalSeconds, WinPeRecordingValidator.MinIntervalSeconds, WinPeRecordingValidator.MaxIntervalSeconds);
        MinutesBox.Text = (total / 60).ToString();
        SecondsBox.Text = (total % 60).ToString();

        switch (initialOptions.Mode)
        {
            case WinPeRecordMode.Video:
                ModeVideoRadio.IsChecked = true;
                break;
            case WinPeRecordMode.Screenshots:
                ModeScreenshotsRadio.IsChecked = true;
                break;
            case WinPeRecordMode.Both:
                ModeBothRadio.IsChecked = true;
                break;
            default:
                ModeNoneRadio.IsChecked = true;
                break;
        }

        ready = true;
        ValidateState();
    }

    public bool Confirmed { get; private set; }
    public WinPeRecordingOptions ResultOptions { get; private set; } = new();

    private WinPeRecordMode SelectedMode =>
        ModeVideoRadio.IsChecked == true ? WinPeRecordMode.Video :
        ModeScreenshotsRadio.IsChecked == true ? WinPeRecordMode.Screenshots :
        ModeBothRadio.IsChecked == true ? WinPeRecordMode.Both :
        WinPeRecordMode.None;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ready)
        {
            ValidateState();
        }
    }

    private void Interval_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ready)
        {
            ValidateState();
        }
    }

    private void ValidateState()
    {
        var needsInterval = SelectedMode is WinPeRecordMode.Screenshots or WinPeRecordMode.Both;
        IntervalPanel.IsEnabled = needsInterval;
        if (!needsInterval)
        {
            IntervalErrorText.Text = "";
            ApplyRecordButton.IsEnabled = true;
            return;
        }

        if (!int.TryParse(MinutesBox.Text.Trim(), out var minutes)
            || !int.TryParse(SecondsBox.Text.Trim(), out var seconds))
        {
            IntervalErrorText.Text = "Enter valid integer minutes and seconds.";
            ApplyRecordButton.IsEnabled = false;
            return;
        }

        var (isValid, _, error) = WinPeRecordingValidator.ValidateInterval(minutes, seconds);
        IntervalErrorText.Text = error ?? "";
        ApplyRecordButton.IsEnabled = isValid;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var total = 60;
        if (SelectedMode is WinPeRecordMode.Screenshots or WinPeRecordMode.Both
            && int.TryParse(MinutesBox.Text.Trim(), out var m)
            && int.TryParse(SecondsBox.Text.Trim(), out var s))
        {
            var (_, sec, _) = WinPeRecordingValidator.ValidateInterval(m, s);
            total = sec;
        }

        ResultOptions = new WinPeRecordingOptions(SelectedMode, total);
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
