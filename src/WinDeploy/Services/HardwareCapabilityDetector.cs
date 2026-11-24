using System.Runtime.Intrinsics.X86;
using Felix.WindowSetup.Core;
using Microsoft.Win32;

namespace Felix.WindowSetup.Services;

public static class HardwareCapabilityDetector
{
    public static CpuCapabilities CurrentCpu() =>
        new(Popcnt.IsSupported, Sse42.IsSupported);

    public static SecureBootHostState DetectSecureBoot(FirmwareMode firmware)
    {
        if (firmware != FirmwareMode.UefiGpt)
        {
            return new SecureBootHostState(false, false, null, null);
        }

        var secureBootEnabled = ReadDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is int sb
            ? sb == 1
            : (bool?)null;

        var ca2023 = ReadDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing", "WindowsUEFICA2023Capable") is int cap
            ? cap >= 1
            : (bool?)null;

        return new SecureBootHostState(true, secureBootEnabled, ca2023, TrustsCa2011: null);
    }

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
