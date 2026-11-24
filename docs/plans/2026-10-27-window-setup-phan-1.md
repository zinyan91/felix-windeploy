# Window Setup phần 1 — kế hoạch triển khai

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa Window Setup lên giao diện `Felix.UI` với ba khối Source/Target/Options, bản đồ đĩa hai chế độ, Find Source, SWM, hộp review riêng, giữ mục boot có sẵn, và nghiệm thu end-to-end UEFI lẫn BIOS trên VM.

**Architecture:** Mọi quy tắc (định dạng nguồn, đủ điều kiện của đĩa, gán vai trò partition, tóm tắt review, so mục boot) là hàm thuần trong `WindowSetup.Core`, test không dựng WPF. App WPF chỉ đọc đĩa/nguồn qua các interface dịch vụ, nối ViewModel với Core và vẽ bằng `Felix.UI`. Luồng ghi (`InstallStorageCoordinator`, WinNTSetup, `WindowsBootRepair`) giữ nguyên khung, chỉ sửa những chỗ spec yêu cầu.

**Tech Stack:** .NET 10, WPF, `Felix.UI` (FeatureWindow, ThemeService, LanguageService, ProportionalSegmentPanel), `Felix.Partition.Core` (NativeStorageReader, DiskPart, WindowsBootRepair), DISM, WinNTSetup 5.4.2, Hyper-V cho nghiệm thu.

**Spec:** [docs/specs/2026-10-27-window-setup-phan-1-design.md](../specs/2026-10-27-window-setup-phan-1-design.md)

## Global Constraints

- Chạy trong FelixPE không mạng; không PowerShell, WMI, CIM trong app. Đọc/ghi đĩa qua `Felix.Partition.Core`.
- Ngoài PE chỉ ghi VHD/VHDX (`IsVirtualDisk` hiện có); đĩa khác hiện mờ, lý do "Chỉ ghi trong FelixPE".
- Mọi chuỗi giao diện nằm trong `Resources/Lang/{en,vi}/Strings_WindowSetup_{en,vi}.xaml`, khoá tiền tố `WS_`. Lỗi từ engine/hệ thống hiện nguyên văn.
- Không tooltip, không dòng giải thích dưới ô, không ô khoá "sắp có". Khối Options chỉ có Unattend.xml.
- Cửa sổ dùng `FeatureWindow`; phải vừa vùng làm việc 1024×768.
- Layout xoá đĩa lấy số từ `InstallDiskLayoutBuilder` (EFI 260 MiB, MSR 16 MiB, BIOS System 550 MiB, Recovery 1024 MiB, tối thiểu 32 GiB). Windows partition tối thiểu 20 GiB; Recovery tối thiểu 750 MiB; EFI/System tối thiểu 100 MiB.
- Sửa gì trong `src/Felix.UI` phải chạy `PackUriAudit`, `tests/Felix.Partition.UiTests` và `tests/PartitionRegression` (CLAUDE.md, bẫy 4).
- Chuỗi đường dẫn thiết bị `\\.\…` trong C# viết bằng công cụ sửa tệp, không qua heredoc (bẫy 5).
- Không commit khi người dùng chưa cho phép. Bước "Checkpoint" chỉ chạy test; nếu được phép commit thì `git add` đúng các tệp của task.
- Test Core dùng `Assert(bool, string)` tự viết trong `window-setup/tests/WindowSetup.Core.Tests`, đăng ký trong `Program.cs`.

## Review Focus

1. Ổ USB FelixPE có ký tự ổ và chứa luôn image người dùng chọn: đĩa đó phải mờ với lý do FelixPE, không bao giờ bấm được ở cả hai chế độ (Task 2 có test).
2. Đổi image sang ổ khác sau khi đã chọn đích: đĩa cũ chứa image phải hết mờ, đĩa mới chứa image phải mờ và lựa chọn đích trên đó bị huỷ (Task 7 có test ViewModel).
3. Bộ SWM thiếu một phần giữa (`install2.swm` mất): báo lỗi rõ phần thiếu, không cho Start (Task 1 có test).
4. Refresh sau khi đĩa bị rút hoặc layout đổi: lựa chọn cũ không còn khớp offset/GUID phải bị bỏ, không âm thầm trỏ sang partition khác (Task 7 có test).
5. Đĩa MBR khi PE boot UEFI (và GPT khi BIOS): chế độ Install to partition không chọn được partition nào trên đĩa đó, thẻ đĩa ghi lý do; Erase disk vẫn chọn được (Task 2 có test).

---

## Cấu trúc tệp

`window-setup/src/WindowSetup.Core/` (thuần, có unit test):

- `SourceImages.cs` (mới): loại nguồn, bộ SWM, tên bản theo build.
- `WindowsImageInspection.cs` (sửa): edition có kiến trúc/version/build; parser kết quả `/Index`.
- `TargetModel.cs` (mới): facts đĩa/partition, lý do chặn, vai trò, quy tắc đủ điều kiện, gợi ý vai trò.
- `TargetSummary.cs` (mới): tóm tắt dưới bản đồ và dữ liệu hộp review.
- `BootMenu.cs` (mới): đọc identifier từ `bcdedit /enum all /v`, so trước/sau.
- `SourceSearch.cs` (mới): quy tắc bỏ thư mục, lọc image, quét cây thư mục.
- `InstallModels.cs`, `WindowSetupPreflight.cs`, `InstallationExecution.cs`, `InstallPlan.cs` (sửa).

`window-setup/src/WindowSetup/`:

- `Services/` (mới): `ITargetSource`, `IImageInspector`, `ISourceScanner`, `FirmwareDetector`, cài đặt thật.
- `StorageTargetLoader.cs` (sửa thành `NativeTargetSource`), `WindowsSourceInspector.cs`, `InstallStorageCoordinator.cs`, `WindowSetupInstallationPlatform.cs` (sửa).
- `ViewModels/` (mới): `WindowSetupViewModel` (chuyển từ gốc), `SourceViewModel`, `FindSourceViewModel`, `TargetViewModel`, `DiskRowViewModel`, `PartitionCardViewModel`, `ReviewViewModel`.
- `Views/` (mới): `MainWindow.xaml` (chuyển từ gốc), `SourceSection.xaml`, `TargetSection.xaml`, `OptionsSection.xaml`, `FindSourceDialog.xaml`, `ReviewDialog.xaml`.
- `Resources/Lang/{en,vi}/`, `Resources/window-setup.ico`.

Test: `window-setup/tests/WindowSetup.Core.Tests/` (thêm tệp test theo task), `window-setup/tests/WindowSetup.IntegrationTests/` (sửa), `window-setup/tests/WindowSetup.UiTests/` (mới), `window-setup/tests/vm/WindowSetupVm.ps1` (mới).

---

### Task 1: Nguồn ISO/WIM/ESD/SWM và thông tin edition đầy đủ

**Files:**
- Create: `window-setup/src/WindowSetup.Core/SourceImages.cs`
- Modify: `window-setup/src/WindowSetup.Core/WindowsImageInspection.cs`, `window-setup/src/WindowSetup.Core/WindowSetupPreflight.cs`, `window-setup/src/WindowSetup.Core/InputFileInspection.cs`
- Test: `window-setup/tests/WindowSetup.Core.Tests/SourceImageTests.cs`

**Interfaces:**
- Produces:
  - `enum SourceImageKind { Iso, Wim, Esd, Swm }`
  - `static SourceImageKind? SourceImages.KindOf(string path)`
  - `sealed record SwmSet(string FirstPart, IReadOnlyList<string> Parts, IReadOnlyList<int> MissingParts) { bool IsComplete }`
  - `static SwmSet SourceImages.ResolveSwm(string firstPart, IEnumerable<string> filesInDirectory)`: phần đầu là `name.swm`, phần k là `name{k}.swm` (k ≥ 2); `MissingParts` là các k thiếu nằm giữa 2 và phần lớn nhất tìm thấy.
  - `static string? SourceImages.ReleaseName(int build)`: 19044→"21H2", 19045→"22H2", 22000→"21H2", 22621→"22H2", 22631→"23H2", 26100→"24H2", 26200→"25H2"; build khác trả `null`.
  - `WindowsImageEdition(int Index, string Name, string? Architecture, string? Version, int? ServicePackBuild)` với `DisplayName` dạng `5 · Windows 11 Pro · x64 · 23H2 (22631.6060)`; thiếu tên bản thì `… · x64 · 22631.6060`.
  - `static (string? Architecture, string? Version, int? ServicePackBuild) DismWimInfoParser.ParseIndex(string output)`: đọc dòng `Architecture : x64`, `Version : 10.0.22631`, `ServicePack Build : 6060`.

- [ ] **Step 1: Viết test thất bại** trong `SourceImageTests.Run()` và gọi từ `Program.cs`:

```csharp
Assert(SourceImages.KindOf(@"E:\a\install.SWM") == SourceImageKind.Swm, "swm kind");
Assert(SourceImages.KindOf(@"E:\a\setup.txt") is null, "unknown kind");
var set = SourceImages.ResolveSwm(@"E:\s\install.swm",
    [@"E:\s\install.swm", @"E:\s\install2.swm", @"E:\s\install4.swm"]);
Assert(!set.IsComplete && set.MissingParts.SequenceEqual([3]), "missing middle part");
Assert(SourceImages.ResolveSwm(@"E:\s\install.swm", [@"E:\s\install.swm", @"E:\s\install2.swm"]).Parts.Count == 2, "two parts");
Assert(SourceImages.ReleaseName(22631) == "23H2" && SourceImages.ReleaseName(12345) is null, "release names");
var info = DismWimInfoParser.ParseIndex("Architecture : x64\r\nVersion : 10.0.22631\r\nServicePack Build : 6060\r\n");
Assert(info == ("x64", "10.0.22631", 6060), "index parse");
Assert(new WindowsImageEdition(5, "Windows 11 Pro", "x64", "10.0.22631", 6060).DisplayName
    == "5 · Windows 11 Pro · x64 · 23H2 (22631.6060)", "display name");
var swmRequest = WindowSetupPreflight.Validate(Request(image: @"E:\s\install.swm", architecture: "x64"));
Assert(!swmRequest.Issues.Any(i => i.Code == "source.unsupported"), "swm accepted");
var unknownArch = WindowSetupPreflight.Validate(Request(image: @"E:\i.wim", architecture: ""));
Assert(unknownArch.Issues.Any(i => i.Code == "source.architecture" && i.IsBlocking), "architecture required");
var anyName = WindowSetupPreflight.Validate(Request(answer: @"E:\my-answer.xml"));
Assert(!anyName.Issues.Any(i => i.Code == "answer-file.name"), "any xml name");
```

`Request(...)` là helper trong tệp test dựng `WindowSetupRequest` hợp lệ ở chế độ Erase disk, chỉ đổi tham số truyền vào.

- [ ] **Step 2: Chạy, xác nhận thất bại**

Run: `dotnet run --project window-setup/tests/WindowSetup.Core.Tests/WindowSetup.Core.Tests.csproj`
Expected: lỗi biên dịch vì `SourceImages` chưa có.

- [ ] **Step 3: Cài đặt**

`SourceImages` như Interfaces. Preflight: `SupportedImageExtensions` thêm `.swm`; `source.architecture` chặn khi Architecture trống hoặc không phải `x64`/`amd64`; bỏ kiểm tên `Autounattend.xml` (`answer-file.name`), giữ kiểm gốc `unattend` qua `InputFileInspection`. `InputFileInspection` với SWM: `ImageExists` chỉ đúng khi `ResolveSwm(...).IsComplete`, và thêm `SwmMissingParts` để preflight báo `source.swm-incomplete` với danh sách số phần thiếu. Sửa test cũ trong `WindowSetupPreflightTests` đang kiểm `answer-file.name` thành kiểm tên tuỳ ý được nhận.

- [ ] **Step 4: Chạy test, xác nhận xanh** — lệnh như Step 2, Expected: `Window Setup Core tests passed.`
- [ ] **Step 5: Checkpoint**

---

### Task 2: Mô hình đích, lý do chặn và quy tắc vai trò

**Files:**
- Create: `window-setup/src/WindowSetup.Core/TargetModel.cs`
- Test: `window-setup/tests/WindowSetup.Core.Tests/TargetModelTests.cs`

**Interfaces:**
- Produces:
  - `sealed record TargetPartitionFacts(int Number, long Offset, long Size, string Letter, string Label, string FileSystem, string Guid, string VolumeSerial, bool IsEfi, bool IsMsr, bool IsRecovery, bool IsActive, bool IsBitLocker, bool IsUnallocated, bool HasWindows)`
  - `sealed record TargetDiskFacts(int Number, string Id, string Serial, string Model, string Bus, string Style, long Size, bool HoldsPeMedia, bool RunsWindows, bool IsReadOnly, bool IsOffline, bool IsDynamic, bool IsVirtual, IReadOnlyList<TargetPartitionFacts> Partitions)`
  - `enum DiskBlockReason { None, FelixPe, RunningWindows, ReadOnly, Offline, Dynamic, HoldsSetupFiles, DesktopPhysicalDisk }` — thứ tự ưu tiên khi nhiều lý do: theo thứ tự khai báo.
  - `enum PartitionModeBlock { None, StyleMismatch, NoSystemPartition }`
  - `enum PartitionRole { Windows, System, Recovery }`
  - `static DiskBlockReason TargetRules.Evaluate(TargetDiskFacts disk, IReadOnlySet<char> setupFileLetters, bool isWinPe)`
  - `static PartitionModeBlock TargetRules.PartitionModeBlock(TargetDiskFacts disk, FirmwareMode bootFirmware)`: UEFI cần GPT, BIOS cần MBR; không có partition nào nhận được vai trò System thì `NoSystemPartition`.
  - `static bool TargetRules.CanTakeRole(TargetDiskFacts disk, TargetPartitionFacts partition, PartitionRole role, FirmwareMode bootFirmware)`
  - `static bool TargetRules.CanErase(TargetDiskFacts disk)`: `Evaluate == None` và `Size ≥ InstallDiskLayoutBuilder.MinimumDiskBytes`; partition BitLocker không chặn.
  - `sealed record RoleSelection(TargetPartitionFacts Windows, TargetPartitionFacts System, TargetPartitionFacts? Recovery)` — `Recovery == null` nghĩa là "Không dùng".
  - `static RoleSelection? PartitionRoles.Suggest(TargetDiskFacts disk, TargetPartitionFacts windows, FirmwareMode bootFirmware)`: GPT chọn ESP FAT32 đầu tiên; MBR chọn partition đang active nếu nhận được vai trò System, nếu không thì chính partition Windows; Recovery là partition Recovery đầu tiên ≥ 750 MiB, không có thì `null`. Trả `null` nếu `windows` không nhận vai trò Windows.
  - `static RoleSelection PartitionRoles.Assign(RoleSelection current, TargetDiskFacts disk, TargetPartitionFacts partition, PartitionRole role, FirmwareMode bootFirmware)`: gán Windows thì gợi ý lại từ đầu; gán System/Recovery chỉ thay vai trò đó; ném `InvalidOperationException` nếu `CanTakeRole` sai.

Quy tắc `CanTakeRole` (spec, mục Install to partition):
- Mọi vai trò: không `IsUnallocated`, không `IsBitLocker`, đĩa `Evaluate == None` và `PartitionModeBlock` (bỏ qua `NoSystemPartition` khi đang xét chính vai trò System) `== None`.
- Windows: không EFI/MSR/Recovery, `Size ≥ 20 GiB`.
- System, UEFI: `IsEfi` và FAT32, `Size ≥ 100 MiB`.
- System, BIOS: NTFS hoặc FAT32, không MSR/Recovery, `Size ≥ 100 MiB`; được trùng partition Windows.
- Recovery: `IsRecovery`, `Size ≥ 750 MiB`.

- [ ] **Step 1: Viết test thất bại** — các ca (dựng facts bằng helper trong tệp test):

```csharp
Assert(TargetRules.Evaluate(Disk(holdsPe: true), Letters('E'), true) == DiskBlockReason.FelixPe, "pe wins over files");
Assert(TargetRules.Evaluate(Disk(letters: "E"), Letters('E'), true) == DiskBlockReason.HoldsSetupFiles, "setup files");
Assert(TargetRules.Evaluate(Disk(isVirtual: false), Letters(), false) == DiskBlockReason.DesktopPhysicalDisk, "desktop physical");
Assert(TargetRules.PartitionModeBlock(Disk(style: "MBR"), FirmwareMode.UefiGpt) == PartitionModeBlock.StyleMismatch, "style mismatch");
var gpt = GptWindowsDisk(); // EFI 260M FAT32, MSR, Windows 238G NTFS, Recovery 1G, Data 226G
Assert(PartitionRoles.Suggest(gpt, Part(gpt, "Windows"), FirmwareMode.UefiGpt) is { System.Label: "EFI", Recovery.Label: "Recovery" }, "gpt suggest");
Assert(!TargetRules.CanTakeRole(gpt, Part(gpt, "Data"), PartitionRole.Recovery, FirmwareMode.UefiGpt), "data cannot be recovery");
Assert(!TargetRules.CanTakeRole(gpt, Locked(Part(gpt, "Data")), PartitionRole.Windows, FirmwareMode.UefiGpt), "bitlocker partition blocked");
var single = MbrSinglePartition(); // một NTFS 60G, chưa active
var mbr = PartitionRoles.Suggest(single, single.Partitions[0], FirmwareMode.BiosMbr)!;
Assert(mbr.System == mbr.Windows && mbr.Recovery is null, "mbr system equals windows");
Assert(TargetRules.PartitionModeBlock(Disk(style: "GPT", partitions: [DataOnly()]), FirmwareMode.UefiGpt) == PartitionModeBlock.NoSystemPartition, "no esp");
Assert(!TargetRules.CanErase(Disk(size: 16L << 30)) && TargetRules.CanErase(Disk(size: 64L << 30, partitions: [Locked(DataOnly())])), "erase size gate, bitlocker allowed");
```

- [ ] **Step 2: Chạy, xác nhận thất bại** — lệnh Task 1 Step 2, Expected: lỗi biên dịch `TargetRules`.
- [ ] **Step 3: Cài đặt `TargetModel.cs`** theo Interfaces.
- [ ] **Step 4: Chạy test, xác nhận xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 3: Tóm tắt đích, dữ liệu review, preflight cho System trùng Windows

**Files:**
- Create: `window-setup/src/WindowSetup.Core/TargetSummary.cs`
- Modify: `window-setup/src/WindowSetup.Core/WindowSetupPreflight.cs`, `window-setup/src/WindowSetup.Core/InstallModels.cs`, `window-setup/src/WindowSetup.Core/InstallPlan.cs`
- Test: `window-setup/tests/WindowSetup.Core.Tests/TargetSummaryTests.cs`

**Interfaces:**
- Consumes: `TargetDiskFacts`, `RoleSelection` (Task 2).
- Produces:
  - `enum SummaryEffect { Format, AddBootFiles, SetActive, Keep, Create, Erase }`
  - `sealed record SummaryLine(PartitionRole? Role, TargetPartitionFacts? Partition, IReadOnlyList<SummaryEffect> Effects, long PlannedSize, string PlannedFileSystem)`
  - `sealed record TargetPlanSummary(IReadOnlyList<SummaryLine> Lines, IReadOnlyList<TargetPartitionFacts> Kept, IReadOnlyList<TargetPartitionFacts> Lost, IReadOnlyList<TargetPartitionFacts> LostBitLocker)`
  - `static TargetPlanSummary TargetSummaries.ForPartitionMode(TargetDiskFacts disk, RoleSelection roles)`: Windows → `Format`; System → `AddBootFiles` (+`SetActive` nếu BIOS và chưa active); khi System trùng Windows thì một dòng có cả `Format, AddBootFiles, SetActive?`; Recovery → `Format`; mọi partition khác (trừ khoảng trống) vào `Kept`.
  - `static TargetPlanSummary TargetSummaries.ForEraseMode(TargetDiskFacts disk, FirmwareMode firmware)`: dòng `Create` theo đúng hằng số `InstallDiskLayoutBuilder` (UEFI: EFI 260 MiB FAT32, MSR 16 MiB, Windows phần còn lại NTFS, Recovery 1024 MiB NTFS; BIOS: System 550 MiB NTFS, Windows, Recovery 1024 MiB); mọi partition hiện có vào `Lost`, partition BitLocker vào thêm `LostBitLocker`.
  - `InstallTarget` thêm `string DiskModel = ""`, `string DiskBus = ""` để review và hộp xác nhận không phải tra lại.
- Preflight: `target.duplicate` cho phép đúng một trường hợp `Firmware == BiosMbr` và `EfiSystemPartition.Identity == WindowsPartition.Identity`; ở trường hợp đó bỏ kiểm `target.system-capacity` riêng.
- `InstallPlanBuilder`: bước Erase là `Erase Disk {DiskNumber}` (đã có); bước System trùng Windows ghi một bước `Format … and add boot files`.

- [ ] **Step 1: Viết test thất bại**: tóm tắt GPT có `Kept == [Data]`, Windows có `Format`; MBR một partition có đúng một dòng với `Format, AddBootFiles, SetActive`; Erase UEFI có 4 dòng `Create` với kích thước 260/16/còn lại/1024 MiB và `LostBitLocker` chứa partition khoá; preflight chấp nhận System trùng Windows với BIOS nhưng vẫn báo `target.duplicate` với UEFI.
- [ ] **Step 2: Chạy, xác nhận thất bại.**
- [ ] **Step 3: Cài đặt** theo Interfaces.
- [ ] **Step 4: Chạy test, xác nhận xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 4: Journal, log ra đĩa đích, mục boot được giữ

**Files:**
- Create: `window-setup/src/WindowSetup.Core/BootMenu.cs`
- Modify: `window-setup/src/WindowSetup.Core/InstallationExecution.cs`, `window-setup/src/WindowSetup.Core/InstallModels.cs`, `window-setup/src/WindowSetup/WindowSetupInstallationPlatform.cs`, `window-setup/src/WindowSetup/WindowSetupViewModel.cs`
- Test: `window-setup/tests/WindowSetup.Core.Tests/RunnerTests.cs` (chuyển `TestInstallationRunnerAsync` sang đây), `window-setup/tests/WindowSetup.Core.Tests/BootMenuTests.cs`

**Interfaces:**
- Produces:
  - `sealed record BcdEntry(string Identifier, string Description, string Device, string OsDevice)`
  - `static IReadOnlyList<BcdEntry> BootMenu.Parse(string enumAllVerboseOutput)`: tách theo khối của `bcdedit /enum all /v`, lấy `identifier`, `description`, `device`, `osdevice`.
  - `static IReadOnlyList<BcdEntry> BootMenu.Missing(IReadOnlyList<BcdEntry> before, IReadOnlyList<BcdEntry> after, string formattedDevice)`: mục có trong `before`, không có trong `after` (so `Identifier`), trừ mục có `Device` hoặc `OsDevice` bằng `formattedDevice` (dạng `partition=W:`).
  - `IInstallationPlatform` thêm `Task<IReadOnlyList<BcdEntry>> ReadBootEntriesAsync(PreparedInstallation installation, CancellationToken token)` (trả rỗng khi chưa có store) và `Task<string?> ExportRunLogAsync(PreparedInstallation installation, string runDirectory)` (trả đường dẫn đã chép, `null` nếu không chép được).
  - `InstallationVerificationResult` thêm `IReadOnlyList<string> MissingBootEntries`; `Succeeded` yêu cầu danh sách này rỗng.
  - `InstallationOutcome` thêm `string? ExportedLogDirectory`.
  - `InstallJournal` thêm `void UseRequest(WindowSetupRequest request)`; runner gọi sau `PrepareTargetAsync` để journal ghi partition thật (sửa lỗi Recovery `none`).
- Hành vi runner: ở chế độ `ExistingPartition`, đọc mục boot sau khi chuẩn bị đĩa, trước apply; sau `ConfigureBootAsync` đọc lại và đưa `Missing(...)` vào verification. Log chép sang `{Windows}:\Windows\Logs\WindowSetup\{tên thư mục run}` ở cả đường thành công và thất bại (khi đã có `prepared`), sau khi journal ghi trạng thái cuối.
- `InstallOptions.ClearBootMenu` mặc định `false`; ViewModel không đặt `true`.
- Tiến trình apply báo bằng `InstallRunStage.ApplyWindows` + `Running` như cũ; UI (Task 10) hiển thị chuỗi `WS_Stage_ApplyWindows_Running` = "WinNTSetup is running. Check its window if it asks for input." / "WinNTSetup đang chạy. Xem cửa sổ của nó nếu nó hỏi."

- [ ] **Step 1: Viết test thất bại**: `BootMenu.Parse` trên một mẫu `bcdedit /enum all /v` có `{bootmgr}`, hai mục Windows Boot Loader (một `device partition=W:`, một `device partition=D:`) trả 3 mục; `Missing(before, afterThiếuMụcD, "partition=W:")` trả mục D, còn thiếu mục W thì không báo; runner giả (fake platform trong test) ở chế độ ExistingPartition mà `ReadBootEntriesAsync` lần sau thiếu một mục → outcome thất bại với lỗi chứa identifier mục đó; journal sau Erase ghi `RecoveryPartitionIdentity` khác `none`; fake `ExportRunLogAsync` được gọi một lần cả khi thất bại sau chuẩn bị đĩa.
- [ ] **Step 2: Chạy, xác nhận thất bại.**
- [ ] **Step 3: Cài đặt** Core theo Interfaces; trong `WindowSetupInstallationPlatform`: `ReadBootEntriesAsync` chạy `bcdedit /store {System}:\EFI\Microsoft\Boot\BCD` (UEFI) hoặc `{System}:\Boot\BCD` (BIOS) `/enum all /v` qua `SystemTools`, trả rỗng nếu store chưa có; `ExportRunLogAsync` chép đệ quy, bắt mọi lỗi I/O và trả `null`.
- [ ] **Step 4: Chạy test, xác nhận xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 5: Dịch vụ đọc đĩa, đọc nguồn và chuẩn bị đĩa theo quy tắc mới

**Files:**
- Create: `window-setup/src/WindowSetup/Services/ITargetSource.cs`, `Services/NativeTargetSource.cs` (thay `StorageTargetLoader.cs`), `Services/IImageInspector.cs`, `Services/FirmwareDetector.cs`
- Modify: `window-setup/src/WindowSetup/WindowsSourceInspector.cs`, `window-setup/src/WindowSetup/InstallStorageCoordinator.cs`, `window-setup/src/WindowSetup.Core/WinNtSetupCommand.cs`
- Test: `window-setup/tests/WindowSetup.IntegrationTests/Program.cs`, `window-setup/tests/WindowSetup.Core.Tests/WindowSetupPreflightTests.cs`

**Interfaces:**
- Consumes: `TargetDiskFacts`, `SourceImages`, `DismWimInfoParser.ParseIndex`.
- Produces:
  - `interface ITargetSource { Task<IReadOnlyList<TargetDiskFacts>> LoadAsync(CancellationToken token); }` — `NativeTargetSource` trả mọi đĩa (không lọc), kể cả khoảng trống chưa phân vùng (`IsUnallocated = true`), `RunsWindows` khi có partition `IsBoot`, `IsVirtual` theo danh sách model hiện có, `Bus` từ `StorageDisk`.
  - `interface IImageInspector { Task<WindowsImageInspection> InspectAsync(string path, CancellationToken token); }` — `WindowsSourceInspector` cài interface này: ISO tìm `sources\install.wim`, `.esd`, rồi `.swm`; WIM/ESD/SWM gọi `/Get-WimInfo` rồi `/Get-WimInfo /Index:n` cho từng index. SWM thiếu phần thì ném lỗi nêu số phần thiếu.
  - `static FirmwareMode FirmwareDetector.Current()` qua `GetFirmwareType` (kernel32): `FirmwareTypeUefi` → `UefiGpt`, còn lại `BiosMbr`.
  - `static IReadOnlySet<char> SetupFileLetters.Of(string? imagePath, string appDirectory, string? answerPath)` (đặt trong Core, `SourceImages.cs`): ký tự ổ của image (và mọi phần SWM), thư mục app (`AppContext.BaseDirectory`), answer file.
- Coordinator: `RefuseSourceOverlap` dùng `SetupFileLetters.Of`; `CopyAnswerFile` giữ tên tệp gốc thay vì đổi thành `Autounattend.xml`; khi System trùng Windows (BIOS) thì dùng chung một ký tự ổ, không format lần hai, vẫn `MarkSystemPartitionActiveAsync`; Erase disk không chặn vì BitLocker. `WinNtSetupCommandBuilder` với SWM truyền `-source:` là phần đầu `.swm` (WinNTSetup tự tìm các phần sau; Task 11 xác nhận bằng VM).

- [ ] **Step 1: Viết test thất bại**: integration `--source <path>` (thay `--iso`, vẫn nhận `--iso` như bí danh) in edition với kiến trúc và build, và assert mọi edition có `Architecture == "x64"` và `ServicePackBuild > 0`; unit test `SetupFileLetters.Of(@"E:\s\install.swm", @"C:\app\", @"F:\a.xml")` = {C, E, F}.
- [ ] **Step 2: Chạy, xác nhận thất bại**

Run: `dotnet run --project window-setup/tests/WindowSetup.IntegrationTests/WindowSetup.IntegrationTests.csproj -- --source "D:\en-us_windows_11_business_editions_version_23h2_updated_oct_2025_x64_dvd_c9a85ebe.iso"`
Expected: FAIL (`--source` chưa có hoặc Architecture trống).

- [ ] **Step 3: Cài đặt** theo Interfaces. Bài layout VHDX hiện có chuyển sang dùng `NativeTargetSource`.
- [ ] **Step 4: Chạy test (quyền Administrator)** — `--source` với ISO xanh; bài layout VHDX mặc định (không tham số) in hai dòng `PASS UefiGpt`, `PASS BiosMbr`; unit test xanh.
- [ ] **Step 5: Checkpoint**

---

### Task 6: Find Source

**Files:**
- Create: `window-setup/src/WindowSetup.Core/SourceSearch.cs`, `window-setup/src/WindowSetup/Services/ISourceScanner.cs`
- Test: `window-setup/tests/WindowSetup.Core.Tests/SourceSearchTests.cs`

**Interfaces:**
- Produces:
  - `sealed record SourceCandidate(string Path, SourceImageKind Kind, long Size)` — `Size` của SWM là tổng các phần.
  - `static bool SourceSearch.SkipDirectory(string name)`: `Windows`, `Program Files`, `Program Files (x86)`, `$Recycle.Bin`, `System Volume Information` (không phân biệt hoa thường).
  - `static bool SourceSearch.IsExcludedImage(string path, IReadOnlyCollection<string> peMediaRoots)`: tên `boot.wim`, `winre.wim`; hoặc nằm dưới một root media FelixPE; hoặc là phần SWM thứ 2 trở đi.
  - `static IAsyncEnumerable<SourceCandidate> SourceSearch.ScanAsync(IEnumerable<string> roots, IReadOnlyCollection<string> peMediaRoots, CancellationToken token)`: duyệt cây, bỏ thư mục không đọc được, không theo reparse point.
  - `interface ISourceScanner { IAsyncEnumerable<SourceCandidate> ScanAsync(CancellationToken token); }` — cài đặt thật lấy root từ `DriveInfo.GetDrives()` có `IsReady`, bỏ `X:\`; root media FelixPE là ổ của đĩa `HoldsPeMedia`.
- Lọc "chỉ bộ cài Windows" làm ở `FindSourceViewModel` (Task 7): với mỗi ứng viên gọi `IImageInspector`, lỗi hoặc 0 edition thì bỏ dòng.

- [ ] **Step 1: Viết test thất bại** trên cây tạm: `a\Win11.iso`, `deep\x\y\install.wim`, `Windows\hidden.iso`, `sources\boot.wim`, `split\install.swm` + `split\install2.swm`, `pe\sources\boot.wim` dưới root PE. Kết quả đúng 3 ứng viên: ISO, WIM, SWM (một dòng, Size = tổng hai phần). Huỷ token giữa chừng thì dừng không ném lỗi ngoài `OperationCanceledException`.
- [ ] **Step 2: Chạy, xác nhận thất bại.**
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy test, xác nhận xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 7: ViewModel cho Source, Find Source và Target

**Files:**
- Create: `window-setup/src/WindowSetup/ViewModels/ObservableObject.cs`, `SourceViewModel.cs`, `FindSourceViewModel.cs`, `TargetViewModel.cs`, `DiskRowViewModel.cs`, `PartitionCardViewModel.cs`
- Modify: chuyển `window-setup/src/WindowSetup/WindowSetupViewModel.cs` vào `ViewModels/`, nối các ViewModel con.
- Create: `window-setup/tests/WindowSetup.UiTests/WindowSetup.UiTests.csproj` (console STA, tham chiếu `WindowSetup.csproj`), `Program.cs` với tham số `--viewmodels`
- Test: `window-setup/tests/WindowSetup.UiTests/ViewModelTests.cs`

**Interfaces:**
- Consumes: `ITargetSource`, `IImageInspector`, `ISourceScanner`, `FirmwareMode bootFirmware`, `bool isWinPe`, quy tắc Task 2–3.
- Produces:
  - `SourceViewModel`: `string ImagePath`, `ObservableCollection<WindowsImageEdition> Editions`, `WindowsImageEdition? Edition`, `bool IsValid`, `string? Error`, `Task SelectImageAsync(string path)`.
  - `FindSourceViewModel`: `ObservableCollection<SourceCandidateRow> Results` (`SourceCandidateRow`: `Candidate`, `int? EditionCount`), `bool IsScanning`, `Task StartAsync()`, `void Cancel()`, `SourceCandidateRow? Selected`.
  - `TargetViewModel`: `InstallTargetMode Mode`, `FirmwareMode BootFirmware`, `FirmwareMode EraseFirmware` (mặc định = `BootFirmware`), `ObservableCollection<DiskRowViewModel> Disks`, `DiskRowViewModel? EraseDisk`, `RoleSelection? Roles`, `TargetPlanSummary? Summary`, `bool IsValid`, `void SelectPartition(PartitionCardViewModel card)`, `void AssignRole(PartitionCardViewModel card, PartitionRole role)`, `void SetRecoveryUnused()`, `void SelectDiskForErase(DiskRowViewModel row)`, `Task RefreshAsync()`, `void UpdateSetupFiles(IReadOnlySet<char> letters)`, `InstallTarget ToInstallTarget()`.
  - `DiskRowViewModel`: `TargetDiskFacts Facts`, `DiskBlockReason Block`, `PartitionModeBlock PartitionBlock`, `ObservableCollection<PartitionCardViewModel> Cards`, `IReadOnlyList<SummaryLine>? PlannedLayout`.
  - `PartitionCardViewModel`: `TargetPartitionFacts Facts`, `PartitionRole? Role`, `bool IsSelected`, `bool CanSelect`, `double Weight` (= `Size`).
  - `WindowSetupViewModel`: `SourceViewModel Source`, `TargetViewModel Target`, `string? AnswerPath`, `bool CanStart`, `bool IsBusy`, `WindowSetupRequest CreateRequest()`.
- `RefreshAsync` giữ lựa chọn chỉ khi đĩa (Number, Id, Serial, Size, Style) và partition (Offset, Size, Guid, VolumeSerial) còn khớp; không khớp thì bỏ lựa chọn. Refresh không đụng Source/Options.

- [ ] **Step 1: Viết test thất bại** (fake `ITargetSource`/`IImageInspector`):

```csharp
var vm = Create(disks: [GptWindowsDisk(0), UsbData(1, letter: 'E')]);
await vm.Target.RefreshAsync();
await vm.Source.SelectImageAsync(@"E:\Win11.iso");
Assert(vm.Target.Disks[1].Block == DiskBlockReason.HoldsSetupFiles, "image disk greyed");
vm.Target.SelectPartition(Card(vm, disk: 0, label: "Windows"));
Assert(vm.Target.Roles is { System.Label: "EFI" } && vm.Target.IsValid, "roles suggested");
await vm.Source.SelectImageAsync(@"C:\other.iso"); // C: thuộc Disk 0
Assert(vm.Target.Roles is null && vm.Target.Disks[1].Block == DiskBlockReason.None, "selection dropped when image moves");
fakeTargets.Next = [GptWindowsDiskWithShiftedWindows(0)];
await vm.Target.RefreshAsync();
Assert(vm.Target.Roles is null, "stale selection dropped on refresh");
vm.Target.Mode = InstallTargetMode.EraseDisk;
vm.Target.SelectDiskForErase(vm.Target.Disks[0]);
Assert(vm.Target.Summary!.Lines.Count == 4 && vm.Target.Summary.Lost.Count > 0, "erase preview");
Assert(vm.CanStart == (vm.Source.IsValid && vm.Target.IsValid), "start gate");
```

- [ ] **Step 2: Chạy, xác nhận thất bại**

Run: `dotnet run --project window-setup/tests/WindowSetup.UiTests/WindowSetup.UiTests.csproj -- --viewmodels`
Expected: lỗi biên dịch hoặc FAIL.

- [ ] **Step 3: Cài đặt** theo Interfaces. `ObservableObject` theo mẫu `SetField` đang dùng trong `WindowSetupViewModel`.
- [ ] **Step 4: Chạy test, xác nhận xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 8: Khung cửa sổ trên Felix.UI, theme, ngôn ngữ, icon và test UI

**Files:**
- Modify: `window-setup/src/WindowSetup/WindowSetup.csproj`, `App.xaml`, `App.xaml.cs`; chuyển `MainWindow.xaml(.cs)` vào `Views/`
- Create: `window-setup/src/WindowSetup/Resources/Lang/en/Strings.en.xaml`, `Strings_WindowSetup_en.xaml`, `Resources/Lang/vi/Strings.vi.xaml`, `Strings_WindowSetup_vi.xaml`, `Resources/window-setup.ico`, `window-setup/scripts/New-WindowSetupIcon.ps1`, `Views/OptionsSection.xaml`
- Create: `window-setup/tests/WindowSetup.UiTests/LayoutProbe.cs`; thêm `--layout`, `--packuri` vào `Program.cs` của project tạo ở Task 7
- Modify: `tests/Felix.Partition.UiTests/PackUriAudit.cs` (nhận danh sách root qua tham số)

**Interfaces:**
- Produces:
  - `WindowSetup.csproj`: `ProjectReference` tới `src/Felix.UI/Felix.UI.csproj`; `Compile Include` link `src/Shared/UserDataPaths.cs` và `src/Shared/Infrastructure/Preferences/FilePreferenceStore.cs` như `Felix.Partition.csproj:39-40`; `ApplicationIcon` = `Resources\window-setup.ico`.
  - `App.OnStartup`: `ThemeService.Initialize("WindowSetup", new FilePreferenceStore())`, `ThemeService.LoadOrDefault()`, `LanguageService.Initialize("WindowSetup", …)`, `LanguageService.LoadOrDefault()`; merge `Fallback.xaml`, `Controls.xaml`, `FluentIcons.xaml` của Felix.UI và `Resources/Lang/en/Strings.en.xaml` (mẫu `src/Felix.Partition/App.xaml`).
  - `MainWindow : FeatureWindow`, `Width=1120 Height=820 MinWidth=720 MinHeight=560`; thanh tiêu đề có nút EN/VI (`LanguageService.Apply(…, save: true)`) và nút Sáng/Tối (`ThemeService.SetChoice(new ThemeChoice(ColorMode.Light|Dark))`).
  - `MainWindow(WindowSetupViewModel viewModel)` — app thật dựng ViewModel với dịch vụ thật; test dựng với fake.
  - `PackUriAudit.Run(Action<bool,string> check, string root, IReadOnlyList<(string Folder, string Assembly)> roots)`; Felix.Partition.UiTests truyền hai root cũ; WindowSetup.UiTests link tệp này và truyền `("window-setup/src/WindowSetup", "WindowSetup")`, `("src/Felix.UI", "Felix.UI")`.
  - Icon: `New-WindowSetupIcon.ps1` vẽ glyph Fluent "Windows" (4 ô vuông) màu Primary của Felix.UI lên nền trong suốt ở 16/32/48/256 px và ghi `.ico`; tệp `.ico` được commit cùng script.
- `WindowSetup.UiTests` (STA): `--layout` dựng `MainWindow` với fake, đặt `Width=1024, Height=728`, Measure/Arrange, kiểm `ActualHeight > 0` (bẫy 19) cho: tiêu đề, ba khối, nút Start, nút Find Source, bản đồ đĩa có ít nhất 2 hàng; đổi theme Dark rồi Light và ngôn ngữ VI rồi EN không ném lỗi và chuỗi `WS_Start` đổi giá trị; `--packuri` chạy audit; không tham số chạy tất cả.

- [ ] **Step 1: Viết test UI thất bại** (`LayoutProbe`, `--packuri`).
- [ ] **Step 2: Chạy, xác nhận thất bại**

Run: `dotnet run --project window-setup/tests/WindowSetup.UiTests/WindowSetup.UiTests.csproj`
Expected: FAIL.

- [ ] **Step 3: Cài đặt** khung: ba khối có tiêu đề đánh số dùng style section của Felix.UI; `OptionsSection` chỉ có Unattend.xml + Browse… (bộ lọc `XML files (*.xml)|*.xml`); thanh dưới có Start, dòng trạng thái, đường dẫn log.
- [ ] **Step 4: Chạy test, xác nhận xanh**; chạy thêm `dotnet run --project tests/Felix.Partition.UiTests` (PackUriAudit với chữ ký mới) và `tests/PartitionRegression` vì đã sửa tệp trong `tests/Felix.Partition.UiTests`.
- [ ] **Step 5: Checkpoint**

---

### Task 9: Khối Source, hộp Find Source, khối Target

**Files:**
- Create: `window-setup/src/WindowSetup/Views/SourceSection.xaml(.cs)`, `Views/FindSourceDialog.xaml(.cs)`, `Views/TargetSection.xaml(.cs)`
- Modify: `Views/MainWindow.xaml`, các tệp chuỗi EN/VI
- Test: `window-setup/tests/WindowSetup.UiTests/LayoutProbe.cs`

**Interfaces:**
- Consumes: ViewModel Task 7.
- Source: ô Image + Browse… (bộ lọc `*.iso;*.wim;*.esd;*.swm`) + Find Source; ô Edition hiện `DisplayName`; chấm trạng thái xanh/đỏ cạnh nhãn (không chữ phụ). Lỗi đọc nguồn hiện nguyên thông điệp dưới dạng hộp lỗi của Felix.UI.
- Find Source: hộp modal có bảng Path / Type / Size / Editions, Cancel trong lúc quét, Close và Use; Use điền ô Image rồi gọi `SelectImageAsync`.
- Target: hàng tiêu đề có nút chế độ hai trạng thái, nhãn `Firmware: UEFI|BIOS`, Refresh. Mỗi đĩa một hàng: thẻ đĩa (số, model, bus · style · dung lượng; lý do chặn nếu có), các thẻ partition trong `ProportionalSegmentPanel` (mẫu `src/Felix.Partition/Views/PartitionManagerView.xaml:139-160`) với tên/ký tự, FS, dung lượng, thanh đã dùng; thẻ có vai trò hiện nhãn vai trò; thẻ đang chọn có ba nút Windows/EFI/Recovery (chữ EFI đổi thành System khi BIOS). Đĩa bị chặn mờ và không nhận click. Chế độ Erase: click thẻ đĩa thay hàng bằng `PlannedLayout` viền cam, hiện nút UEFI/GPT – BIOS/MBR và cảnh báo đỏ (số đĩa, model, dung lượng, partition sẽ mất, partition BitLocker). Dưới bản đồ: ô tóm tắt từ `Summary` với chấm trạng thái và liên kết "Không dùng" cho Recovery.
- Mọi chuỗi qua `DynamicResource` khoá `WS_…`.

- [ ] **Step 1: Mở rộng `LayoutProbe` (thất bại trước)**: với fake 3 đĩa (GPT Windows, USB FelixPE, MBR data), ở 1024×728: hàng FelixPE có `IsEnabled == false` và hiện text lý do; click thẻ Windows (gọi command qua ViewModel) làm ô tóm tắt có `ActualHeight > 0`; chuyển Erase và chọn đĩa MBR thì 4 thẻ planned có `ActualHeight > 0` và dòng cảnh báo hiện; hộp Find Source mở được và có 4 cột.
- [ ] **Step 2: Chạy, xác nhận thất bại.**
- [ ] **Step 3: Cài đặt** views.
- [ ] **Step 4: Chạy `WindowSetup.UiTests` xanh.**
- [ ] **Step 5: Checkpoint**

---

### Task 10: Hộp review, xác nhận xoá đĩa và luồng Start

**Files:**
- Create: `window-setup/src/WindowSetup/ViewModels/ReviewViewModel.cs`, `Views/ReviewDialog.xaml(.cs)`
- Modify: `Views/MainWindow.xaml(.cs)`, `ViewModels/WindowSetupViewModel.cs`, tệp chuỗi
- Test: `window-setup/tests/WindowSetup.UiTests/ViewModelTests.cs`, `LayoutProbe.cs`

**Interfaces:**
- Consumes: `TargetPlanSummary`, `WindowSetupRequest`, `InstallationOutcome.ExportedLogDirectory`.
- Produces:
  - `ReviewViewModel(WindowSetupRequest request, TargetPlanSummary summary)`: `IReadOnlyList<(string LabelKey, string Value)> Rows` theo thứ tự Windows, Source, Disk, Windows partition, EFI/System, Recovery, Keep, Setup; `string WarningKey` + tham số; `bool RequiresDiskNumber` (Erase); `string TypedDiskNumber`; `bool CanConfirm` (Erase: `TypedDiskNumber.Trim() == DiskNumber.ToString()`).
  - Không in `DiskIdentity` hay chuỗi có `|` ở bất kỳ dòng nào.
- Start chỉ bật khi `CanStart`. Bấm Start → `ReviewDialog` (Back / Install Windows). Xác nhận → `InstallAsync`; trong lúc chạy khoá ba khối, giữ chặn đóng cửa sổ; dòng trạng thái và thanh tiến trình hiện chuỗi `WS_Stage_{Stage}_{State}`; kết thúc hiện hộp kết quả với đường dẫn log đã chép (`ExportedLogDirectory`, nếu `null` thì `RunDirectory`). Bỏ hai `MessageBox` cũ.

- [ ] **Step 1: Viết test thất bại**: review cho request Erase có `RequiresDiskNumber`, `CanConfirm` sai khi gõ "2" với Disk 1 và đúng khi gõ " 1 "; review partition mode có dòng Keep liệt kê Data; không dòng nào chứa `|`; `LayoutProbe` mở `ReviewDialog` ở 1024×728 và nút Install Windows có `ActualHeight > 0`.
- [ ] **Step 2: Chạy, xác nhận thất bại.**
- [ ] **Step 3: Cài đặt.**
- [ ] **Step 4: Chạy toàn bộ: Core tests, UiTests, integration VHDX (Administrator).**
- [ ] **Step 5: Checkpoint**

---

### Task 11: Harness VM và nghiệm thu end-to-end

**Files:**
- Create: `window-setup/tests/vm/WindowSetupVm.ps1`, `window-setup/tests/vm/README.md`
- Modify: `window-setup/README.md`, `docs/plans/2026-10-23-window-setup.md`, `CLAUDE.md` (chỉ khi có bẫy mới)
- Create: `docs/specs/2026-10-2x-window-setup-phan-1-nghiem-thu.md`

**Interfaces:**
- `WindowSetupVm.ps1 -Action <Prepare|Start|Screen|Run|Text|Key|Click|Collect|Remove> -Fixture <dir> [-Generation 1|2] [-Scenario <name>]`, dot-source `tests/PartitionRegression/HyperVFixtureConsole.ps1` để gửi lệnh/chụp màn hình (Gen1 dùng 640×480 và `Confirm-HyperVFixtureDvdBoot`, bẫy 16). Fixture ở `.tmp/window-setup-vm-<run-id>`, VM tên `WindowSetup-<8 ký tự>`; không đụng VM `FelixPE-26.5.2`. `Prepare` kiểm SHA-256 PE ISO `FECC625C65772313049492CBA74CF63D3B0941D0A97C5EC1CDD35CB1193DCE75`, ghi `fixture.json` (hash PE ISO, Windows ISO, dll app, WinNTSetup).
- Payload VHDX 48 GiB NTFS chứa: bản publish, ISO Windows (`B0B640C6…57DC8C`), `install.wim` trích từ ISO, bộ SWM tạo bằng `dism /Split-Image /FileSize:2000`, `Autounattend.xml` thử (mật khẩu ngẫu nhiên lưu trong fixture, bản lưu bằng chứng che mật khẩu), thư mục `sample-data` có checksum tính trên host.
- `Collect`: khi VM Off, gắn payload và đích chỉ đọc trên host, chép log, tính SHA-256 tệp mẫu trên đích, xuất `manifest.json`.

Các lượt (mỗi lượt một VM/đích riêng, chụp màn hình từng bước):

1. Gen2, đích 64 GiB trắng: nguồn ISO chọn bằng Find Source, Erase disk UEFI/GPT, có Unattend → boot tới desktop; kiểm `hostname`, `whoami`, nhóm Administrators, `EditionID`, `reagentc /info` Enabled.
2. Gen2, đích là đĩa của lượt 1 sau khi trong PE: shrink Windows để tạo partition `Data`, chép `sample-data` vào, thêm mục BCD `bcdedit /store S:\EFI\Microsoft\Boot\BCD /copy {default} /d "Keep entry"` rồi đặt `device`/`osdevice` của mục mới thành partition Data. Nguồn WIM, Install to partition chọn Windows, không Unattend → boot tới màn hình OOBE đầu tiên; sau tắt máy: checksum `sample-data` khớp, `bcdedit /store … /enum all` còn "Keep entry"; ô tóm tắt và review ghi Data ở Keep.
3. Gen1, đích 64 GiB trắng: nguồn SWM, Erase disk BIOS/MBR, có Unattend → desktop, cùng các kiểm như lượt 1.
4. Gen1, đích 64 GiB MBR một partition NTFS chưa active (dựng trên host): nguồn ISO, Install to partition (System trùng Windows, review ghi đặt active), có Unattend → desktop.
5. Trong lượt 1 hoặc 3 trước khi cài: đĩa payload (chứa image, app, answer file) mờ với lý do; Find Source liệt kê đúng ISO, WIM, SWM trên payload và không có `boot.wim`.

- [ ] **Step 1: Viết `WindowSetupVm.ps1`**, chạy `Prepare` + `Start` + `Screen` cho Gen2 và Gen1, xác nhận PE lên desktop ở cả hai.
- [ ] **Step 2: Chạy lượt 1–5**, lưu ảnh, log, `manifest.json` vào `artifacts/hyperv/FelixPE-26.5.2/validation/window-setup-phan-1-<run-id>/`.
- [ ] **Step 3: Viết biên bản nghiệm thu** (hash, kết quả từng lượt, lỗi mới thấy, giới hạn còn lại); cập nhật README và plan gốc; soát văn bản theo skill `writing`.
- [ ] **Step 4: Dọn**: gỡ VM thử và fixture `.tmp` sau khi đã lưu bằng chứng và kiểm không còn VM nào tham chiếu tới VHD; xoá đồ tạm.
- [ ] **Step 5: Checkpoint**
