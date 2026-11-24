using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Views;

public partial class IntegrityCheckDialog : Window
{
    private readonly string imagePath;
    private readonly CancellationTokenSource cts = new();

    public IntegrityCheckDialog(string imagePath)
    {
        this.imagePath = imagePath;
        InitializeComponent();
        FileValue.Text = Path.GetFileName(imagePath);
        SizeValue.Text = File.Exists(imagePath)
            ? $"{new FileInfo(imagePath).Length / (1024d * 1024 * 1024):0.00} GB"
            : "—";
        Loaded += IntegrityCheckDialog_Loaded;
        Closing += (_, _) => cts.Cancel();
    }

    private async void IntegrityCheckDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Sha256Box.Text = "—";
            return;
        }

        try
        {
            var sidecar = SourceIntegrityChecker.TryReadSidecarSha256(imagePath);
            var sigText = await Task.Run(() => CheckSignature(imagePath), cts.Token);
            SignatureValue.Text = sigText;

            var progress = new Progress<int>(pct => HashProgressBar.Value = pct);
            var sha256 = await SourceIntegrityChecker.ComputeSha256Async(imagePath, progress, cts.Token);
            Sha256Box.Text = sha256;
            CopyHashButton.IsEnabled = true;

            var matchLabel = TryFindResource("WS_Integrity_Match") as string ?? "Matched";
            var mismatchLabel = TryFindResource("WS_Integrity_Mismatch") as string ?? "Mismatch";
            var noneLabel = TryFindResource("WS_Integrity_None") as string ?? "Not found";

            SidecarValue.Text = string.IsNullOrWhiteSpace(sidecar)
                ? noneLabel
                : string.Equals(sha256, sidecar, StringComparison.OrdinalIgnoreCase)
                    ? $"{matchLabel} ({sidecar[..12]}…)"
                    : $"{mismatchLabel} ({sidecar[..12]}…)";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Sha256Box.Text = ex.Message;
        }
    }

    private string CheckSignature(string path)
    {
        var noneLabel = Dispatcher.Invoke(() => TryFindResource("WS_Integrity_None") as string ?? "Not found");
        IsoMountLease? mount = null;
        try
        {
            var targetFile = path;
            if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                mount = IsoMountLease.AttachAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                var setupExe = Path.Combine(mount.RootPath, "setup.exe");
                var bootMgr = Path.Combine(mount.RootPath, "bootmgr.efi");
                targetFile = File.Exists(setupExe) ? setupExe : (File.Exists(bootMgr) ? bootMgr : "");
            }

            if (string.IsNullOrEmpty(targetFile) || !File.Exists(targetFile))
            {
                return noneLabel;
            }

            var cert = X509CertificateLoader.LoadCertificateFromFile(targetFile);
            return cert.Subject;
        }
        catch
        {
            return noneLabel;
        }
        finally
        {
            mount?.Dispose();
        }
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Sha256Box.Text))
        {
            Clipboard.SetText(Sha256Box.Text);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
