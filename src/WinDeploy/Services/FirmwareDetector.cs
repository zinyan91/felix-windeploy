using System.Runtime.InteropServices;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public static class FirmwareDetector
{
    public static FirmwareMode Current() =>
        GetFirmwareType(out var type) && type == FirmwareTypeUefi
            ? FirmwareMode.UefiGpt
            : FirmwareMode.BiosMbr;

    private const uint FirmwareTypeUefi = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}
