namespace Felix.WindowSetup.Core;

public enum SecureBootStatus
{
    Pass,
    Warning,
    Fail
}

public sealed record SecureBootHostState(
    bool IsUefi,
    bool? SecureBootEnabled,
    bool? TrustsCa2023,
    bool? TrustsCa2011);

public sealed record SecureBootAssessment(
    SecureBootStatus Status,
    string Code,
    string Message,
    bool PreferBootEx);

public static class SecureBootCheck
{
    public static SecureBootAssessment Evaluate(SecureBootHostState host, bool hasBootExAsset)
    {
        if (!host.IsUefi || host.SecureBootEnabled == false)
        {
            return new(
                SecureBootStatus.Pass,
                "secureboot.off",
                "Secure Boot is disabled or legacy BIOS mode is active.",
                PreferBootEx: false);
        }

        if (host.SecureBootEnabled == true && host.TrustsCa2023 == true && hasBootExAsset)
        {
            return new(
                SecureBootStatus.Pass,
                "secureboot.ca2023-ready",
                "Firmware trusts Windows UEFI CA 2023 and the image provides CA 2023 boot files.",
                PreferBootEx: true);
        }

        if (host.SecureBootEnabled == true && host.TrustsCa2023 == true && !hasBootExAsset)
        {
            return new(
                SecureBootStatus.Warning,
                "secureboot.ca2023-missing-asset",
                "Firmware trusts CA 2023, but the image lacks bootmgfw_EX.efi; standard boot files will be used.",
                PreferBootEx: false);
        }

        if (host.SecureBootEnabled == true && host.TrustsCa2023 == false && host.TrustsCa2011 == true)
        {
            return new(
                SecureBootStatus.Warning,
                "secureboot.ca2011-only",
                "Firmware trusts CA 2011 only; standard boot files will be used.",
                PreferBootEx: false);
        }

        return new(
            SecureBootStatus.Warning,
            "secureboot.db-unreadable",
            "Secure Boot CA 2023 trust state could not be verified; standard boot files will be used.",
            PreferBootEx: false);
    }
}
