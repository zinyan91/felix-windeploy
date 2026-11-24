using System.Windows;
using Felix.Infrastructure.Preferences;
using Felix.UI.Services;

namespace Felix.WindowSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeService.Initialize("FelixWinDeploy", new FilePreferenceStore());
        ThemeService.LoadOrDefault();
        LanguageService.Initialize("FelixWinDeploy", new FilePreferenceStore(), "FelixWinDeploy");
        LanguageService.LoadOrDefault();

        var (resumePath, resumeBackupPath) = ParseLaunchArgs(e.Args);
        MainWindow = !string.IsNullOrWhiteSpace(resumeBackupPath)
            ? new Views.ResumeBackupWindow(resumeBackupPath)
            : !string.IsNullOrWhiteSpace(resumePath)
                ? new Views.ResumeInstallWindow(resumePath)
                : new Views.MainWindow();
        MainWindow.Show();
    }

    private static (string? ResumePath, string? ResumeBackupPath) ParseLaunchArgs(string[] args)
    {
        string? resume = null;
        string? resumeBackup = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--resume", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                resume ??= args[i + 1];
            }
            else if (string.Equals(args[i], "--resume-backup", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                resumeBackup ??= args[i + 1];
            }
        }
        return (resume, resumeBackup);
    }
}
