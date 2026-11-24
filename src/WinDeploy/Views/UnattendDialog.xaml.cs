using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;
using Felix.WindowSetup.Core;
using Microsoft.Win32;

namespace Felix.WindowSetup.Views;

public partial class UnattendDialog : Window
{
    private readonly bool initialized;

    public UnattendDialog() : this(null)
    {
    }

    public UnattendDialog(UnattendSpec? initialSpec)
    {
        InitializeComponent();
        var spec = initialSpec ?? new UnattendSpec(Password: UnattendGenerator.GenerateRandomPassword(), ClearPasswordOnFirstLogon: true);
        UsernameBox.Text = spec.Username;
        PasswordBox.Text = string.IsNullOrEmpty(spec.Password) ? UnattendGenerator.GenerateRandomPassword() : spec.Password;
        ComputerNameBox.Text = spec.ComputerName;
        TimeZoneBox.Text = string.IsNullOrWhiteSpace(spec.TimeZone) ? TimeZoneInfo.Local.Id : spec.TimeZone;
        LocaleBox.Text = spec.Locale;
        SkipOobeCheck.IsChecked = spec.SkipOobe;
        AutoLogonCheck.IsChecked = spec.AutoLogonOnce;
        ClearPasswordCheck.IsChecked = spec.ClearPasswordOnFirstLogon;
        BypassMsAccountCheck.IsChecked = spec.BypassMicrosoftAccount;
        initialized = true;
        UpdatePreview();
    }

    public string? ResultPath { get; private set; }
    public UnattendSpec? ResultSpec { get; private set; }

    private UnattendSpec CurrentSpec() => new(
        UsernameBox.Text,
        PasswordBox.Text,
        ComputerNameBox.Text,
        TimeZoneBox.Text,
        LocaleBox.Text,
        SkipOobeCheck.IsChecked == true,
        AutoLogonCheck.IsChecked == true,
        ClearPasswordCheck.IsChecked == true,
        BypassMsAccountCheck.IsChecked == true);

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (initialized)
        {
            UpdatePreview();
        }
    }

    private void Check_Changed(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            UpdatePreview();
        }
    }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e) =>
        PasswordBox.Text = UnattendGenerator.GenerateRandomPassword();

    private void UpdatePreview()
    {
        PreviewBox.Text = UnattendGenerator.Generate(CurrentSpec());
    }

    private bool TryValidatePreviewXml(out string xml)
    {
        xml = PreviewBox.Text;
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var doc = XDocument.Load(reader);
            if (!string.Equals(doc.Root?.Name.LocalName, "unattend", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "The root element must be unattend.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }
        catch (XmlException ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidatePreviewXml(out var xml))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = "unattend.xml"
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, xml);
            ResultPath = dialog.FileName;
            Close();
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidatePreviewXml(out var xml))
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "FelixPE", "WindowSetupUnattend");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"unattend-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        ResultPath = path;
        ResultSpec = CurrentSpec();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
