using System.Xml.Linq;

namespace Felix.WindowSetup.Core;

public static class WifiProfileParser
{
    public static string? ParseConnectedSsid(string netshInterfacesOutput)
    {
        if (string.IsNullOrWhiteSpace(netshInterfacesOutput))
        {
            return null;
        }

        var isConnected = false;
        string? ssid = null;

        void ResetBlock()
        {
            isConnected = false;
            ssid = null;
        }

        foreach (var rawLine in netshInterfacesOutput.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                if (isConnected && !string.IsNullOrWhiteSpace(ssid))
                {
                    return ssid;
                }

                ResetBlock();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (key.Equals("State", StringComparison.OrdinalIgnoreCase))
            {
                isConnected = value.Equals("connected", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                ssid = value;
            }
        }

        return isConnected && !string.IsNullOrWhiteSpace(ssid) ? ssid : null;
    }

    public static bool IsValidWifiProfileXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return false;
        }

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root is null || !root.Name.LocalName.Equals("WLANProfile", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var nameElement = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(nameElement?.Value);
        }
        catch
        {
            return false;
        }
    }
}
