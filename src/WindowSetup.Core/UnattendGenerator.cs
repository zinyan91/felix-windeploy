using System.Security.Cryptography;
using System.Xml.Linq;

namespace Felix.WindowSetup.Core;

public sealed record UnattendSpec(
    string Username = "Admin",
    string Password = "",
    string ComputerName = "*",
    string TimeZone = "SE Asia Standard Time",
    string Locale = "en-US",
    bool SkipOobe = true,
    bool AutoLogonOnce = true,
    bool ClearPasswordOnFirstLogon = false,
    bool BypassMicrosoftAccount = true);

public static class UnattendGenerator
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public static string Generate(UnattendSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var username = string.IsNullOrWhiteSpace(spec.Username) ? "Admin" : spec.Username.Trim();
        var computerName = string.IsNullOrWhiteSpace(spec.ComputerName) ? "*" : spec.ComputerName.Trim();
        var timeZone = string.IsNullOrWhiteSpace(spec.TimeZone) ? "SE Asia Standard Time" : spec.TimeZone.Trim();
        var locale = string.IsNullOrWhiteSpace(spec.Locale) ? "en-US" : spec.Locale.Trim();
        var password = spec.Password ?? "";

        var specialize = new XElement(Ns + "settings",
            new XAttribute("pass", "specialize"),
            Component("Microsoft-Windows-Shell-Setup",
                new XElement(Ns + "ComputerName", computerName),
                new XElement(Ns + "TimeZone", timeZone)));

        if (spec.BypassMicrosoftAccount)
        {
            specialize.Add(Component("Microsoft-Windows-Deployment",
                new XElement(Ns + "RunSynchronous",
                    new XElement(Ns + "RunSynchronousCommand",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Ns + "Order", "1"),
                        new XElement(Ns + "Path", @"reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f")))));
        }

        var shellSetupOobe = Component("Microsoft-Windows-Shell-Setup");

        if (spec.SkipOobe)
        {
            shellSetupOobe.Add(new XElement(Ns + "OOBE",
                new XElement(Ns + "HideEULAPage", "true"),
                new XElement(Ns + "HideOEMRegistrationScreen", "true"),
                new XElement(Ns + "HideOnlineAccountScreens", spec.BypassMicrosoftAccount ? "true" : "false"),
                new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                new XElement(Ns + "ProtectYourPC", "3")));
        }
        else if (spec.BypassMicrosoftAccount)
        {
            shellSetupOobe.Add(new XElement(Ns + "OOBE",
                new XElement(Ns + "HideOnlineAccountScreens", "true")));
        }

        shellSetupOobe.Add(new XElement(Ns + "UserAccounts",
            new XElement(Ns + "LocalAccounts",
                new XElement(Ns + "LocalAccount",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Ns + "Name", username),
                    new XElement(Ns + "DisplayName", username),
                    new XElement(Ns + "Group", "Administrators"),
                    new XElement(Ns + "Password",
                        new XElement(Ns + "Value", password),
                        new XElement(Ns + "PlainText", "true"))))));

        if (spec.AutoLogonOnce)
        {
            shellSetupOobe.Add(new XElement(Ns + "AutoLogon",
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "LogonCount", "1"),
                new XElement(Ns + "Username", username),
                new XElement(Ns + "Password",
                    new XElement(Ns + "Value", password),
                    new XElement(Ns + "PlainText", "true"))));
        }

        if (spec.ClearPasswordOnFirstLogon && !string.IsNullOrEmpty(password))
        {
            var safeUser = username.Replace("\"", "");
            shellSetupOobe.Add(new XElement(Ns + "FirstLogonCommands",
                new XElement(Ns + "SynchronousCommand",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Ns + "Order", "1"),
                    new XElement(Ns + "CommandLine", $"cmd /c net user \"{safeUser}\" \"\""),
                    new XElement(Ns + "Description", "Clear temporary logon password"))));
        }

        var oobe = new XElement(Ns + "settings",
            new XAttribute("pass", "oobeSystem"),
            Component("Microsoft-Windows-International-Core",
                new XElement(Ns + "InputLocale", locale),
                new XElement(Ns + "SystemLocale", locale),
                new XElement(Ns + "UILanguage", locale),
                new XElement(Ns + "UserLocale", locale)),
            shellSetupOobe);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                specialize,
                oobe));

        return doc.Declaration + Environment.NewLine + doc;
    }

    public static string GenerateBypassMicrosoftAccountOnly()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                new XElement(Ns + "settings",
                    new XAttribute("pass", "specialize"),
                    Component("Microsoft-Windows-Deployment",
                        new XElement(Ns + "RunSynchronous",
                            new XElement(Ns + "RunSynchronousCommand",
                                new XAttribute(Wcm + "action", "add"),
                                new XElement(Ns + "Order", "1"),
                                new XElement(Ns + "Path", @"reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f"))))),
                new XElement(Ns + "settings",
                    new XAttribute("pass", "oobeSystem"),
                    Component("Microsoft-Windows-Shell-Setup",
                        new XElement(Ns + "OOBE",
                            new XElement(Ns + "HideOnlineAccountScreens", "true"))))));

        return doc.Declaration + Environment.NewLine + doc;
    }

    public static string GenerateRandomPassword(int length = 12)
    {
        if (length < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Password length must be at least 8.");
        }

        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string all = upper + lower + digits;

        var chars = new char[length];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 3; i < length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static XElement Component(string name, params object[] content) =>
        new(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);
}
