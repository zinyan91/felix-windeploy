namespace Felix.WindowSetup.Core;

public sealed record HardwareDiskSummary(
    int Number,
    string Model,
    string Bus,
    bool IsSolidState,
    long SizeBytes,
    string Style);

public sealed record HardwareSnapshot(
    string CpuName,
    int PhysicalCores,
    int LogicalThreads,
    int MaxClockMhz,
    string Architecture,
    CpuCapabilities CpuCaps,
    long TotalRamBytes,
    string GpuName,
    long? GpuVramBytes,
    string Mainboard,
    string BiosVersion,
    FirmwareMode Firmware,
    bool? SecureBootEnabled,
    bool HasTpm20,
    string TpmSummary,
    IReadOnlyList<HardwareDiskSummary> Disks);

public enum HardwareCheckSeverity
{
    Pass,
    Warning,
    Fail
}

public sealed record HardwareCheckItem(
    string Id,
    string Label,
    string ValueText,
    HardwareCheckSeverity Severity);

public enum EditionRecommendationTier
{
    BestMatch,
    CompatibleWithBypass,
    NotSupported
}

public sealed record RecommendedWindowsOption(
    string EditionName,
    string TargetUse,
    EditionRecommendationTier Tier,
    string BadgeText,
    string ReasonText);

public sealed record HardwareAssessmentReport(
    string PrimaryEdition,
    string PrimaryReason,
    HardwareCheckSeverity OverallSeverity,
    IReadOnlyList<HardwareCheckItem> Checks,
    IReadOnlyList<RecommendedWindowsOption> Options);

public static class HardwareAdvisor
{
    public static HardwareAssessmentReport Evaluate(HardwareSnapshot hw, bool vietnamese)
    {
        ArgumentNullException.ThrowIfNull(hw);

        var ramGiB = hw.TotalRamBytes / (1024d * 1024 * 1024);
        var hasModernCpu = hw.CpuCaps.HasPopcnt && hw.CpuCaps.HasSse42;
        var hasSsd = hw.Disks.Any(d => d.IsSolidState && d.SizeBytes >= 50L * 1024 * 1024 * 1024)
            || hw.Disks.Any(d => d.IsSolidState);
        var largestDiskGiB = hw.Disks.Count > 0
            ? hw.Disks.Max(d => d.SizeBytes) / (1024d * 1024 * 1024)
            : 0;
        var primaryDisk = hw.Disks
            .OrderByDescending(d => d.IsSolidState)
            .ThenByDescending(d => d.SizeBytes)
            .FirstOrDefault();

        var cpuSeverity = !hasModernCpu || hw.PhysicalCores < 2
            ? HardwareCheckSeverity.Warning
            : HardwareCheckSeverity.Pass;
        var cpuValue = hasModernCpu
            ? (vietnamese
                ? $"{hw.PhysicalCores} nhân / {hw.LogicalThreads} luồng · POPCNT & SSE4.2"
                : $"{hw.PhysicalCores} cores / {hw.LogicalThreads} threads · POPCNT & SSE4.2")
            : (vietnamese
                ? $"{hw.PhysicalCores} nhân / {hw.LogicalThreads} luồng · Thiếu POPCNT/SSE4.2"
                : $"{hw.PhysicalCores} cores / {hw.LogicalThreads} threads · Missing POPCNT/SSE4.2");

        var ramSeverity = ramGiB < 4.0
            ? HardwareCheckSeverity.Fail
            : ramGiB < 7.5
                ? HardwareCheckSeverity.Warning
                : HardwareCheckSeverity.Pass;
        var ramValue = $"{ramGiB:0.#} GB";

        var firmwareSeverity = hw.Firmware == FirmwareMode.UefiGpt
            ? HardwareCheckSeverity.Pass
            : HardwareCheckSeverity.Warning;
        var firmwareValue = hw.Firmware == FirmwareMode.UefiGpt ? "UEFI / GPT" : "Legacy BIOS / MBR";

        var tpmSeverity = hw.HasTpm20 ? HardwareCheckSeverity.Pass : HardwareCheckSeverity.Warning;
        var tpmValue = string.IsNullOrWhiteSpace(hw.TpmSummary)
            ? (hw.HasTpm20 ? "TPM 2.0" : (vietnamese ? "Không có / đang tắt" : "Absent / disabled"))
            : hw.TpmSummary;

        var sbSeverity = hw.SecureBootEnabled == true
            ? HardwareCheckSeverity.Pass
            : HardwareCheckSeverity.Warning;
        var sbValue = hw.SecureBootEnabled switch
        {
            true => vietnamese ? "Đang bật" : "Enabled",
            false => vietnamese ? "Đang tắt" : "Disabled",
            null => vietnamese ? "Không hỗ trợ" : "Unsupported"
        };

        var diskSeverity = primaryDisk is null || largestDiskGiB < 50
            ? HardwareCheckSeverity.Fail
            : primaryDisk.IsSolidState
                ? HardwareCheckSeverity.Pass
                : HardwareCheckSeverity.Warning;
        var diskValue = primaryDisk is null
            ? (vietnamese ? "Không tìm thấy ổ đĩa" : "No internal disk detected")
            : $"{(primaryDisk.IsSolidState ? "SSD / NVMe" : "HDD")} · {primaryDisk.SizeBytes / (1024d * 1024 * 1024):0.#} GB";

        var checks = new List<HardwareCheckItem>
        {
            new("cpu", "CPU", cpuValue, cpuSeverity),
            new("ram", "RAM", ramValue, ramSeverity),
            new("firmware", vietnamese ? "Chuẩn khởi động" : "Boot Firmware", firmwareValue, firmwareSeverity),
            new("tpm", "TPM 2.0", tpmValue, tpmSeverity),
            new("secureboot", "Secure Boot", sbValue, sbSeverity),
            new("disk", vietnamese ? "Ổ đĩa chính" : "Primary Storage", diskValue, diskSeverity)
        };

        var overall = checks.Any(c => c.Severity == HardwareCheckSeverity.Fail)
            ? HardwareCheckSeverity.Fail
            : checks.Any(c => c.Severity == HardwareCheckSeverity.Warning)
                ? HardwareCheckSeverity.Warning
                : HardwareCheckSeverity.Pass;

        var options = BuildRecommendations(hw, ramGiB, hasModernCpu, hasSsd, vietnamese);
        var primary = options.FirstOrDefault(o => o.Tier == EditionRecommendationTier.BestMatch) ?? options[0];

        return new HardwareAssessmentReport(
            primary.EditionName,
            primary.ReasonText,
            overall,
            checks,
            options);
    }

    private static List<RecommendedWindowsOption> BuildRecommendations(
        HardwareSnapshot hw,
        double ramGiB,
        bool hasModernCpu,
        bool hasSsd,
        bool vietnamese)
    {
        var list = new List<RecommendedWindowsOption>();

        if (!hasModernCpu)
        {
            var preferLtsc21 = ramGiB < 8.0 || !hasSsd;
            if (preferLtsc21)
            {
                list.Add(new(
                    "Windows 10 IoT Enterprise LTSC 2021",
                    vietnamese ? "Máy cấu hình thấp / HDD" : "Legacy CPU · low RAM or HDD",
                    EditionRecommendationTier.BestMatch,
                    vietnamese ? "Phù hợp nhất" : "Best match",
                    vietnamese
                        ? "CPU không có SSE4.2/POPCNT nên không chạy được Windows 11 24H2; LTSC 2021 nhẹ RAM/SSD và hỗ trợ dài hạn."
                        : "CPU lacks SSE4.2/POPCNT required by Windows 11 24H2; LTSC 2021 runs smoothly on low RAM/HDD with long-term support."));
                list.Add(new(
                    "Windows 10 Pro / Home 22H2",
                    vietnamese ? "Đủ ứng dụng phổ thông" : "General desktop apps",
                    EditionRecommendationTier.CompatibleWithBypass,
                    vietnamese ? "Tương thích" : "Compatible",
                    vietnamese
                        ? "Cài đặt đầy đủ Microsoft Store và tính năng phổ thông trên CPU cũ."
                        : "Supports all standard desktop apps and Microsoft Store on pre-SSE4.2 CPUs."));
            }
            else
            {
                list.Add(new(
                    "Windows 10 Pro 22H2",
                    vietnamese ? "Làm việc & đa nhiệm" : "Work & general use",
                    EditionRecommendationTier.BestMatch,
                    vietnamese ? "Phù hợp nhất" : "Best match",
                    vietnamese
                        ? "CPU đủ khoẻ nhưng thiếu POPCNT/SSE4.2 của Windows 11 24H2; Windows 10 22H2 tận dụng trọn hiệu năng phần cứng."
                        : "CPU lacks POPCNT/SSE4.2 for Windows 11 24H2; Windows 10 22H2 runs natively at full speed."));
                list.Add(new(
                    "Windows 10 IoT Enterprise LTSC 2021",
                    vietnamese ? "Tối giản, ít tiến trình nền" : "Lean & low overhead",
                    EditionRecommendationTier.CompatibleWithBypass,
                    vietnamese ? "Khuyên dùng nhẹ" : "Lean option",
                    vietnamese
                        ? "Không cài sẵn ứng dụng quảng cáo, ít cập nhật tính năng gây nặng máy."
                        : "Clean footprint with no bundled Store bloatware."));
            }

            list.Add(new(
                "Windows 11 IoT Enterprise LTSC 2024",
                vietnamese ? "Bản rút gọn Windows 11" : "Lean Windows 11",
                EditionRecommendationTier.NotSupported,
                vietnamese ? "Không hỗ trợ" : "Not supported",
                vietnamese ? "Build 26100 yêu cầu CPU có SSE4.2 và POPCNT." : "Build 26100 requires SSE4.2 and POPCNT CPU instructions."));
            list.Add(new(
                "Windows 11 Pro / Home 24H2",
                vietnamese ? "Hệ điều hành mới nhất" : "Latest consumer OS",
                EditionRecommendationTier.NotSupported,
                vietnamese ? "Không hỗ trợ" : "Not supported",
                vietnamese ? "Build 26100 yêu cầu CPU có SSE4.2 và POPCNT." : "Build 26100 requires SSE4.2 and POPCNT CPU instructions."));
            return list;
        }

        if (ramGiB < 7.5 || !hasSsd)
        {
            list.Add(new(
                "Windows 10 IoT Enterprise LTSC 2021",
                vietnamese ? "RAM dưới 8 GB hoặc dùng HDD" : "Under 8 GB RAM or HDD",
                EditionRecommendationTier.BestMatch,
                vietnamese ? "Phù hợp nhất" : "Best match",
                vietnamese
                    ? $"Máy đang có {ramGiB:0.#} GB RAM{(hasSsd ? "" : " và ổ HDD")}; Windows 10 LTSC phản hồi nhanh hơn rõ rệt."
                    : $"With {ramGiB:0.#} GB RAM{(hasSsd ? "" : " on HDD")}, Windows 10 LTSC avoids heavy UI lag and background bloat."));
            list.Add(new(
                "Windows 11 IoT Enterprise LTSC 2024",
                vietnamese ? "Cần giao diện Windows 11 nhẹ" : "Lightweight Windows 11",
                EditionRecommendationTier.CompatibleWithBypass,
                vietnamese ? "Cài được (nhẹ)" : "Runs well (lean)",
                vietnamese
                    ? "Không yêu cầu TPM/Secure Boot, ít app nền hơn nhiều so với bản 24H2 thường."
                    : "Drops TPM/Secure Boot checks and ships without bundled Store apps."));
            list.Add(new(
                "Windows 11 Pro / Home 24H2",
                vietnamese ? "Đầy đủ tính năng 24H2" : "Full Windows 11 24H2",
                EditionRecommendationTier.CompatibleWithBypass,
                vietnamese ? "Cần nâng cấp SSD/RAM" : "Heavy for hardware",
                vietnamese
                    ? "Vẫn cài được qua Unattend tích hợp, nhưng nên có tối thiểu 8 GB RAM và SSD."
                    : "Installs via built-in bypass, though 8+ GB RAM and an SSD are strongly recommended."));
            list.Add(new(
                "Windows 10 Pro 22H2",
                vietnamese ? "Phổ thông ổn định" : "Standard Windows 10",
                EditionRecommendationTier.CompatibleWithBypass,
                vietnamese ? "Tương thích" : "Compatible",
                vietnamese ? "Hoạt động ổn định nếu cần Microsoft Store." : "Stable fallback if full Microsoft Store is required."));
            return list;
        }

        if (hw.Firmware == FirmwareMode.UefiGpt && hw.HasTpm20)
        {
            var proEdition = ramGiB >= 15.5 || hw.PhysicalCores >= 6
                ? "Windows 11 Pro 24H2"
                : "Windows 11 Home / Pro 24H2";
            list.Add(new(
                proEdition,
                vietnamese ? "Đủ chuẩn UEFI + TPM 2.0" : "Native UEFI + TPM 2.0",
                EditionRecommendationTier.BestMatch,
                vietnamese ? "Phù hợp nhất" : "Best match",
                vietnamese
                    ? $"Phần cứng đạt chuẩn Windows 11 ({ramGiB:0.#} GB RAM, SSD/NVMe, UEFI, TPM 2.0), cài sạch không cần bypass."
                    : $"System meets every Windows 11 requirement ({ramGiB:0.#} GB RAM, SSD/NVMe, UEFI, TPM 2.0) natively."));
            list.Add(new(
                "Windows 11 IoT Enterprise LTSC 2024",
                vietnamese ? "Tối ưu tốc độ & độ sạch" : "Zero bloatware workstation",
                EditionRecommendationTier.BestMatch,
                vietnamese ? "Bản sạch khuyên dùng" : "Clean alternative",
                vietnamese
                    ? "Phù hợp kỹ thuật viên muốn máy sạch, ít tiến trình nền và cập nhật ổn định tới 2034."
                    : "Ideal for technicians wanting clean background services and long-term servicing."));
            list.Add(new(
                "Windows 10 IoT Enterprise LTSC 2021",
                vietnamese ? "Cần phần mềm cũ Windows 10" : "Legacy Windows 10 workloads",
                EditionRecommendationTier.CompatibleWithBypass,
                vietnamese ? "Tương thích" : "Compatible",
                vietnamese ? "Dùng khi có phần mềm chuyên dụng hoặc driver cũ." : "Use if specific drivers or industrial software require Windows 10."));
            list.Add(new(
                "Windows 10 Pro 22H2",
                vietnamese ? "Phổ thông ổn định" : "Standard Windows 10",
                EditionRecommendationTier.CompatibleWithBypass,
                vietnamese ? "Tương thích" : "Compatible",
                vietnamese ? "Phiên bản Windows 10 đầy đủ nếu cần tương thích ứng dụng cũ." : "Full consumer Windows 10 release if legacy app compatibility is needed."));
            return list;
        }

        list.Add(new(
            "Windows 11 IoT Enterprise LTSC 2024",
            vietnamese ? "Không cần TPM 2.0 / Secure Boot" : "No TPM 2.0 / Secure Boot required",
            EditionRecommendationTier.BestMatch,
            vietnamese ? "Phù hợp nhất" : "Best match",
            vietnamese
                ? "CPU & RAM đủ mạnh nhưng máy chưa bật TPM 2.0 hoặc đang dùng Legacy; Windows 11 IoT LTSC chạy mượt và tự hợp lệ."
                : "Strong CPU/RAM without active TPM 2.0 or UEFI; IoT LTSC 2024 runs smoothly with relaxed requirements."));
        list.Add(new(
            "Windows 11 Pro / Home 24H2",
            vietnamese ? "Đầy đủ tính năng Windows 11" : "Full Windows 11 24H2",
            EditionRecommendationTier.CompatibleWithBypass,
            vietnamese ? "Dùng Unattend bypass" : "With auto bypass",
            vietnamese
                ? "Tương thích nhờ cấu hình Unattend tích hợp sẵn bỏ qua kiểm tra TPM/RAM/Secure Boot."
                : "Works smoothly using Felix WinDeploy's built-in Unattend hardware bypass."));
        list.Add(new(
            "Windows 10 IoT Enterprise LTSC 2021",
            vietnamese ? "Ổn định nhẹ nhàng" : "Lightweight fallback",
            EditionRecommendationTier.CompatibleWithBypass,
            vietnamese ? "Tương thích" : "Compatible",
            vietnamese ? "Chạy chuẩn không cần bypass trên BIOS Legacy lẫn UEFI." : "Runs natively on both Legacy BIOS and UEFI without bypass."));
        list.Add(new(
            "Windows 10 Pro 22H2",
            vietnamese ? "Phổ thông ổn định" : "Standard Windows 10",
            EditionRecommendationTier.CompatibleWithBypass,
            vietnamese ? "Tương thích" : "Compatible",
            vietnamese ? "Hoạt động mượt không cần bypass TPM trên máy chưa bật Secure Boot." : "Runs natively without TPM bypass on systems without Secure Boot."));
        return list;
    }
}
