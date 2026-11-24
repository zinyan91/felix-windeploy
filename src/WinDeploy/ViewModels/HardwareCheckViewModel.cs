using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed class HardwareCheckViewModel(IHardwareProfileService hardwareService) : ObservableObject
{
    private HardwareSnapshot? snapshot;
    private bool vietnamese = true;
    private bool isLoading;
    private string cpuName = "—";
    private string cpuDetail = "";
    private string ramTotal = "—";
    private string ramDetail = "";
    private string gpuName = "—";
    private string gpuDetail = "";
    private string boardName = "—";
    private string firmwareDetail = "";
    private string primaryEdition = "—";
    private string primaryReason = "";
    private HardwareCheckSeverity overallSeverity = HardwareCheckSeverity.Pass;
    private string? errorMessage;

    public bool IsLoading { get => isLoading; private set => SetField(ref isLoading, value); }
    public string CpuName { get => cpuName; private set => SetField(ref cpuName, value); }
    public string CpuDetail { get => cpuDetail; private set => SetField(ref cpuDetail, value); }
    public string RamTotal { get => ramTotal; private set => SetField(ref ramTotal, value); }
    public string RamDetail { get => ramDetail; private set => SetField(ref ramDetail, value); }
    public string GpuName { get => gpuName; private set => SetField(ref gpuName, value); }
    public string GpuDetail { get => gpuDetail; private set => SetField(ref gpuDetail, value); }
    public string BoardName { get => boardName; private set => SetField(ref boardName, value); }
    public string FirmwareDetail { get => firmwareDetail; private set => SetField(ref firmwareDetail, value); }
    public string PrimaryEdition { get => primaryEdition; private set => SetField(ref primaryEdition, value); }
    public string PrimaryReason { get => primaryReason; private set => SetField(ref primaryReason, value); }
    public HardwareCheckSeverity OverallSeverity { get => overallSeverity; private set => SetField(ref overallSeverity, value); }
    public string? ErrorMessage { get => errorMessage; private set => SetField(ref errorMessage, value); }

    public ObservableCollection<HardwareCheckItem> Checks { get; } = [];
    public ObservableCollection<RecommendedWindowsOption> Options { get; } = [];

    public async Task RefreshAsync(bool isVietnamese, CancellationToken cancellationToken = default)
    {
        vietnamese = isVietnamese;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            snapshot = await hardwareService.ProbeAsync(cancellationToken);
            ApplySnapshot();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SetLanguage(bool isVietnamese)
    {
        vietnamese = isVietnamese;
        if (snapshot is not null)
        {
            ApplySnapshot();
        }
    }

    private void ApplySnapshot()
    {
        if (snapshot is null)
        {
            return;
        }

        CpuName = snapshot.CpuName;
        var ghzText = snapshot.MaxClockMhz > 0 ? $" · {snapshot.MaxClockMhz / 1000d:0.00} GHz" : string.Empty;
        CpuDetail = vietnamese
            ? $"{snapshot.PhysicalCores} nhân / {snapshot.LogicalThreads} luồng{ghzText}"
            : $"{snapshot.PhysicalCores} cores / {snapshot.LogicalThreads} threads{ghzText}";

        var ramGiB = snapshot.TotalRamBytes / (1024d * 1024 * 1024);
        RamTotal = ramGiB > 0 ? $"{ramGiB:0.#} GB" : "—";
        RamDetail = snapshot.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)
            ? "ARM64"
            : snapshot.Architecture.ToLowerInvariant();

        GpuName = snapshot.GpuName;
        GpuDetail = snapshot.GpuVramBytes is > 0
            ? $"{snapshot.GpuVramBytes.Value / (1024d * 1024 * 1024):0.#} GB VRAM"
            : (vietnamese ? "Đồ hoạ tích hợp / cơ bản" : "Integrated / basic display");

        BoardName = snapshot.Mainboard;
        var fwText = snapshot.Firmware == FirmwareMode.UefiGpt ? "UEFI / GPT" : "Legacy BIOS / MBR";
        FirmwareDetail = string.IsNullOrWhiteSpace(snapshot.BiosVersion) ? fwText : $"{fwText} · {snapshot.BiosVersion}";

        var report = HardwareAdvisor.Evaluate(snapshot, vietnamese);
        PrimaryEdition = report.PrimaryEdition;
        PrimaryReason = report.PrimaryReason;
        OverallSeverity = report.OverallSeverity;

        Checks.Clear();
        foreach (var item in report.Checks)
        {
            Checks.Add(item);
        }

        Options.Clear();
        foreach (var opt in report.Options)
        {
            Options.Add(opt);
        }
    }
}
