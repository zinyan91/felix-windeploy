using System.Text;

namespace Felix.WindowSetup.Core;

public sealed record FirstBootScriptSpec(
    bool HasDriversRetry,
    bool HasManualDriversRetry,
    bool HasWifiProfiles,
    bool HasPrinterBackup,
    bool EnableWinRe,
    bool SetBootTimeoutTenSeconds,
    IReadOnlyList<RemoteAppSelection> EnabledRemoteApps,
    string? DesktopLauncherPath,
    bool WriteDesktopSummaryReport);

public static class FirstBootScriptBuilder
{
    public static string BuildSetupCompleteCmd(FirstBootScriptSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var lines = new List<string>
        {
            "@echo off",
            "setlocal EnableExtensions",
            @"set ""LOG=%ProgramData%\FelixWindowSetup\firstboot.log""",
            @"set ""REPORT=%PUBLIC%\Desktop\FelixWindowSetup.txt""",
            @"if not exist ""%ProgramData%\FelixWindowSetup"" mkdir ""%ProgramData%\FelixWindowSetup""",
            "echo [%DATE% %TIME%] Felix WinDeploy First Boot started>> \"%LOG%\""
        };

        if (spec.HasDriversRetry)
        {
            lines.Add(
                @"pnputil.exe /add-driver ""%ProgramData%\FelixWindowSetup\FirstBoot\Drivers\*.inf"" /subdirs /install >> ""%LOG%"" 2>&1");
        }

        if (spec.HasManualDriversRetry)
        {
            lines.Add(
                @"pnputil.exe /add-driver ""%ProgramData%\FelixWindowSetup\FirstBoot\ManualDrivers\*.inf"" /subdirs /install >> ""%LOG%"" 2>&1");
        }

        if (spec.HasWifiProfiles)
        {
            lines.Add(
                @"for %%F in (""%ProgramData%\FelixWindowSetup\FirstBoot\Wifi\*.xml"") do netsh.exe wlan add profile filename=""%%~fF"" user=all >> ""%LOG%"" 2>&1");
        }

        if (spec.HasPrinterBackup)
        {
            lines.Add(
                @"if exist ""%SystemRoot%\System32\spool\tools\PrintBrm.exe"" if exist ""%ProgramData%\FelixWindowSetup\FirstBoot\Printers\Printers.printerExport"" ""%SystemRoot%\System32\spool\tools\PrintBrm.exe"" -R -F ""%ProgramData%\FelixWindowSetup\FirstBoot\Printers\Printers.printerExport"" -O FORCE >> ""%LOG%"" 2>&1");
        }

        if (spec.EnableWinRe)
        {
            lines.Add(@"reagentc.exe /enable >> ""%LOG%"" 2>&1");
        }

        if (spec.SetBootTimeoutTenSeconds)
        {
            lines.Add(@"bcdedit.exe /timeout 10 >> ""%LOG%"" 2>&1");
        }

        foreach (var app in spec.EnabledRemoteApps.Where(a => a.Enabled))
        {
            AppendRemoteAppCommands(lines, app, spec.WriteDesktopSummaryReport);
        }

        if (!string.IsNullOrWhiteSpace(spec.DesktopLauncherPath))
        {
            lines.Add(
                $@"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"" /v FelixWindowSetupDesktop /t REG_SZ /d ""\""{spec.DesktopLauncherPath}\"""" /f >> ""%LOG%"" 2>&1");
        }

        lines.Add("echo [%DATE% %TIME%] Felix WinDeploy First Boot completed>> \"%LOG%\"");
        lines.Add("exit /b 0");
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static string BuildDesktopLauncherCmd(IReadOnlyList<string> launchCommands)
    {
        ArgumentNullException.ThrowIfNull(launchCommands);

        var lines = new List<string>
        {
            "@echo off",
            "setlocal EnableExtensions"
        };

        foreach (var command in launchCommands.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var trimmed = command.Trim();
            lines.Add($@"if exist ""{trimmed}"" start """" ""{trimmed}""");
        }

        lines.Add("exit /b 0");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static void AppendRemoteAppCommands(
        ICollection<string> lines,
        RemoteAppSelection app,
        bool writeReport)
    {
        var fileName = RemoteSupportCatalog.InstallerFileName(app.Kind);
        var url = RemoteSupportCatalog.DownloadUrl(app.Kind);
        var stagedPath = $@"%ProgramData%\FelixWindowSetup\FirstBoot\Remote\{fileName}";

        lines.Add($@"if not exist ""%ProgramData%\FelixWindowSetup\FirstBoot\Remote"" mkdir ""%ProgramData%\FelixWindowSetup\FirstBoot\Remote""");
        lines.Add($@"if not exist ""{stagedPath}"" curl.exe -fsSL ""{url}"" -o ""{stagedPath}"" >> ""%LOG%"" 2>&1");

        switch (app.Kind)
        {
            case RemoteSupportAppKind.AnyDesk:
                lines.Add($@"if exist ""{stagedPath}"" ""{stagedPath}"" --install ""C:\Program Files (x86)\AnyDesk"" --start-with-win --silent --create-shortcuts >> ""%LOG%"" 2>&1");
                if (!string.IsNullOrWhiteSpace(app.Password))
                {
                    lines.Add($@"if exist ""C:\Program Files (x86)\AnyDesk\AnyDesk.exe"" (echo {app.Password}| ""C:\Program Files (x86)\AnyDesk\AnyDesk.exe"" --set-password >> ""%LOG%"" 2>&1)");
                }
                if (writeReport)
                {
                    lines.Add(@"if exist ""C:\Program Files (x86)\AnyDesk\AnyDesk.exe"" for /f ""delims="" %%I in ('""C:\Program Files (x86)\AnyDesk\AnyDesk.exe"" --get-id 2^>nul') do echo AnyDesk ID: %%I>> ""%REPORT%""");
                }
                break;

            case RemoteSupportAppKind.RustDesk:
                lines.Add($@"if exist ""{stagedPath}"" ""{stagedPath}"" --silent-install >> ""%LOG%"" 2>&1");
                if (!string.IsNullOrWhiteSpace(app.Password))
                {
                    lines.Add($@"if exist ""C:\Program Files\RustDesk\rustdesk.exe"" ""C:\Program Files\RustDesk\rustdesk.exe"" --password ""{app.Password}"" >> ""%LOG%"" 2>&1");
                }
                if (writeReport)
                {
                    lines.Add(@"if exist ""C:\Program Files\RustDesk\rustdesk.exe"" for /f ""delims="" %%I in ('""C:\Program Files\RustDesk\rustdesk.exe"" --get-id 2^>nul') do echo RustDesk ID: %%I>> ""%REPORT%""");
                }
                break;

            case RemoteSupportAppKind.UltraViewer:
                lines.Add($@"if exist ""{stagedPath}"" ""{stagedPath}"" /VERYSILENT /NORESTART >> ""%LOG%"" 2>&1");
                break;

            case RemoteSupportAppKind.TeamViewerPortable:
                lines.Add(@"if not exist ""C:\Felix\Remote\TeamViewer"" mkdir ""C:\Felix\Remote\TeamViewer""");
                lines.Add(@"echo [TeamViewer]> ""C:\Felix\Remote\TeamViewer\TeamViewer.ini""");
                lines.Add(@"echo TermsOfUseAccepted=1>> ""C:\Felix\Remote\TeamViewer\TeamViewer.ini""");
                break;
        }
    }
}

public static class InstallSummaryReportBuilder
{
    public static string Build(WindowSetupRequest request, StagedDriverArtifacts? stagedDrivers = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sb = new StringBuilder();
        sb.AppendLine("Felix WinDeploy - Installation Summary");
        sb.AppendLine("=========================================");
        sb.AppendLine($"Edition: {request.Source.Edition}");
        sb.AppendLine($"Source: {request.Source.ImagePath}");
        sb.AppendLine($"Target: Disk {request.Target.DiskNumber} ({request.Target.Firmware})");
        sb.AppendLine($"Windows Partition: {request.Target.WindowsPartition.DisplayName}");
        sb.AppendLine($"System Partition: {request.Target.EfiSystemPartition.DisplayName}");
        sb.AppendLine($"Recovery Partition: {request.Target.RecoveryPartition.DisplayName}");

        if (stagedDrivers is not null)
        {
            sb.AppendLine($"Backed up drivers: {stagedDrivers.StagedDriverCount}");
            if (!string.IsNullOrWhiteSpace(stagedDrivers.WifiProfilePath))
            {
                sb.AppendLine($"Wi-Fi profile staged: {Path.GetFileName(stagedDrivers.WifiProfilePath)}");
            }
            if (!string.IsNullOrWhiteSpace(stagedDrivers.PrinterBackupPath))
            {
                sb.AppendLine($"Printer backup staged: {Path.GetFileName(stagedDrivers.PrinterBackupPath)}");
            }
        }

        if (request.Options?.BootAndRecovery is { } boot)
        {
            sb.AppendLine($"Parallel Windows boot entries: {(boot.AddParallelWindows ? "Yes" : "No")}");
            if (!string.IsNullOrWhiteSpace(boot.ExtraWinPePath))
            {
                sb.AppendLine($"Extra WinPE: {boot.ExtraWinPePath}");
            }
        }

        if (request.Options?.FirstBoot is { } firstBoot)
        {
            var enabledApps = (firstBoot.RemoteApps ?? []).Where(a => a.Enabled).ToList();
            if (enabledApps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Remote Support Applications:");
                foreach (var app in enabledApps)
                {
                    var name = RemoteSupportCatalog.DisplayName(app.Kind);
                    if (RemoteSupportCatalog.RequiresPassword(app.Kind))
                    {
                        sb.AppendLine($"- {name} | Password: {app.Password}");
                    }
                    else
                    {
                        sb.AppendLine($"- {name} | ID and session password appear in its desktop window");
                    }
                }
            }

            if (firstBoot.HasFirstDesktopApp)
            {
                sb.AppendLine();
                sb.AppendLine($"At first desktop: {firstBoot.FirstDesktopApp!.SourcePath}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(@"First Boot log: %ProgramData%\FelixWindowSetup\firstboot.log");
        return sb.ToString();
    }
}
