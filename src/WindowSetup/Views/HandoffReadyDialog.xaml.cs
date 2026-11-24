using System.Windows;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.Views;

public enum HandoffReadyAction
{
    InstallNextRestart,
    RestartNow,
    CancelPreparation
}

public partial class HandoffReadyDialog : Window
{
    public HandoffReadyDialog(PreparedDesktopHandoff prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        InitializeComponent();

        var request = prepared.Session.Request;
        StagingValue.Text = prepared.SessionDirectory;
        EditionValue.Text = request.Source.Edition;
        TargetValue.Text = $"Disk {request.Target.DiskNumber} · {request.Target.DiskModel}".Trim(' ', '·');
        SourceValue.Text = Path.GetFileName(request.Source.ImagePath);
    }

    public HandoffReadyAction SelectedAction { get; private set; } = HandoffReadyAction.InstallNextRestart;

    private void RestartNow_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = HandoffReadyAction.RestartNow;
        Close();
    }

    private void NextRestart_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = HandoffReadyAction.InstallNextRestart;
        Close();
    }

    private void CancelPreparation_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = HandoffReadyAction.CancelPreparation;
        Close();
    }
}
