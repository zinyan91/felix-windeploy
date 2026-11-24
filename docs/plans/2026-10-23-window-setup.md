# Window Setup — kế hoạch app cài Windows trong FelixPE

## Mục tiêu

Tạo một app WPF self-contained tên **Window Setup**, dùng bộ giao diện Felix
Partition. App có một luồng cài Windows duy nhất: người dùng chọn source, disk,
partition và tùy chọn cài đặt trên cùng một màn hình, sau đó bấm `Start`.

Nếu người dùng cung cấp cấu hình **Autounattend.xml**, app chạy toàn bộ
luồng unattended tới desktop và hậu kiểm. Nếu không có answer file, app chạy
quy trình cài Windows bình thường với source và target đã chọn.

App phải mở trực tiếp từ FelixPE, không cần trình duyệt, PowerShell, .NET cài
trên máy đích hoặc thao tác đoán ký tự ổ. Tên hiển thị, shortcut và icon đều
thống nhất là **Window Setup** theo yêu cầu.

Mã nguồn thuộc `D:\Felix Platform\felix-partition\window-setup`, dùng chung
`src/Felix.UI` của repo Partition. Repo FelixPE chỉ nhận bản build để đóng gói.
Mockup hiện tại là một biểu mẫu dọc gồm Bộ cài, Nơi cài, Driver và tùy chọn
Autounattend; app tự đọc disk/partition và không mở Felix Partition để chọn đích.

## Bằng chứng hiện có

- Repo FelixPE đã có WinNTSetup 5.4.2 tại `D:\FelixPE\payload\apps\_WinNTSetup`, gồm EXE,
  thư mục `Lang`, `Tools`, `DISM`, `Diskpart`, `Unattend` và các tệp hỗ trợ.
- Manifest hiện đăng ký WinNTSetup như một app riêng. Khi có Window Setup, mục
  cũ sẽ trở thành engine nội bộ; người dùng chỉ thấy shortcut Window Setup.
- `D:\FelixPE\src\toolkit\Felix.UI` là bản sao rút gọn cũ. So với
  `D:\Felix Platform\felix-partition\src\Felix.UI` ở commit `42da2db`, phép đo
  hiện tại là 94 tệp trùng, 30 tệp khác và 504 tệp còn thiếu trên tổng 628 tệp.
  Vì vậy không được coi bản sao hiện tại là giao diện Partition mới nhất.
- `Felix Partition` có `FeatureWindow`, `WindowHeader`, theme sáng/tối,
  `InstallerShared.xaml`, resource tiếng Việt và các control dùng cho layout
  dạng wizard. Chúng là phần cần tái sử dụng; không bê nguyên toàn bộ tính năng
  phân vùng vào app cài Windows.
- Window Setup tái sử dụng phần lõi partition cần cho việc đọc disk/partition,
  nhận diện identity, kiểm tra an toàn và thao tác format/layout tối thiểu.
  Không bê nguyên màn hình quản lý partition, menu thao tác hàng loạt hoặc
  workflow riêng của Felix Partition vào app cài Windows.
- Các tài liệu hiện tại chỉ xác nhận WinNTSetup mở được và nhập Unattend được;
  áp WIM, tạo tài khoản và boot Windows sau cài chưa được nghiệm thu. Đây là
  khoảng trống phải đóng trước khi gọi automatic mode là hoàn tất.

## Quyết định kiến trúc

### App và project

Tạo ba project mới trong workspace `window-setup` của repo `felix-partition`:

```text
window-setup/
  src/
    WindowSetup/              WPF shell, views, resources, startup
    WindowSetup.Core/         model, planner, validation, XML/command builders
  tests/
    WindowSetup.Core.Tests/   unit tests không dựng WPF
```

`WindowSetup` tham chiếu trực tiếp project `src/Felix.UI` ở gốc repo Partition
và dùng chung `FeatureWindow`/theme. `WindowSetup.Core` không import WPF, `System.Windows`,
`next/*` hay UI API.

### Giao diện Partition

Trước khi viết view:

1. Ghi nhận commit và thay đổi chưa commit của `src/Felix.UI` dùng để build.
2. Tham chiếu trực tiếp UI chung: `FeatureWindow`, shell header, theme
   colors/tokens, buttons, cards, inputs, lists, dialogs và icons; chuỗi riêng
   của Window Setup đặt trong app.
3. Chạy PackUri audit và UI smoke khi nối resource hoặc sửa UI chung. Không copy toàn bộ
   `Felix.Partition` để tránh kéo theo backend phân vùng, menu và resource
   không liên quan.
4. Icon app dùng icon riêng của Window Setup; không lấy icon tạm của Partition.

### Engine cài đặt

WinNTSetup là engine cho cả hai mode trong phiên bản đầu. Window Setup chịu
trách nhiệm:

- đọc nguồn image và đích;
- kiểm tra điều kiện;
- lập kế hoạch;
- tạo cấu hình unattended tạm trong thư mục run;
- truyền tham số đã kiểm tra;
- đọc tiến trình/log;
- hậu kiểm Windows và boot.

Không gọi trực tiếp một chuỗi lệnh chưa có bằng chứng. Task đầu tiên của
implementation là lập bảng command line/exit code của đúng WinNTSetup 5.4.2
đang có trong payload, rồi chạy trên VHDX thử. Nếu engine không hỗ trợ đầy đủ
automatic mode qua command line, dừng ở preflight và chuyển phần apply sang
backend riêng sau một plan bổ sung; không giả vờ rằng mở UI là cài tự động.

## Luồng người dùng

### Màn hình đầu

Một màn hình cài Windows duy nhất:

- Source: chọn ISO/WIM/ESD và edition/index.
- Target: chọn disk, Windows partition, EFI/System partition và Recovery
  partition; với clean install thì hiển thị layout ba vùng sẽ được tạo.
- Driver: chọn backup, Windows cũ, thư mục khác hoặc bỏ qua.
- Options: kiểm tra image, xử lý boot menu, tạo Recovery partition và restart.
- Unattended: để trống nếu muốn chạy cài Windows bình thường; chọn
  `Autounattend.xml` nếu muốn chạy answer file.
- Review: hiển thị thay đổi, cảnh báo xoá/format và nút `Install Windows`.

Thanh đầu dùng `FeatureWindow` và `WindowHeader` của Partition. Giao diện có
Sáng/Tối, tiếng Anh trước; không thêm subtitle hoặc tooltip dài.

### Chọn nguồn

Hỗ trợ:

- thư mục ISO đã mount;
- `install.wim`;
- `install.esd`;
- ISO cài Windows khi WinNTSetup có thể đọc trực tiếp.

Sau khi đọc nguồn, hiển thị index, edition, architecture, build và kích thước.
Nếu có nhiều index, người dùng phải chọn rõ một index; không tự chọn
Professional theo tên gần đúng.

### Chọn đích

Hiển thị theo phong cách bản đồ đĩa của Felix Partition:

- model, serial, bus, dung lượng;
- GPT/MBR;
- ESP/MSR/Recovery/Windows/data;
- BitLocker/Read-only;
- ký tự ổ và volume GUID khi có.

Không dùng ký tự ổ làm định danh duy nhất. Ghim disk number + serial/identity +
offset/volume GUID rồi đọc lại ngay trước khi ghi.

### Luồng cài Windows

Window Setup luôn chuẩn bị context, đọc source/target và chạy preflight. Khi
người dùng bấm `Start`, app truyền source/target đã kiểm tra cho engine, ghi lại
command line và chạy job cài Windows. Không có tab cài thủ công riêng.

Nếu có `Autounattend.xml`, app dùng answer file cho các pass Setup, tạo account,
cấu hình OOBE và các tác vụ hậu cài đã chọn. Nếu không có answer file, app giữ
luồng cài Windows bình thường và không tự thêm cấu hình unattended.

Sau khi job kết thúc, Window Setup không dựa vào mã thoát một mình. Nó đọc log
và hậu kiểm target: có `Windows\System32`, `winload`, boot files/BCD phù hợp và
volume vẫn đúng identity.

### Hai preset đích cài

Preset ban đầu chỉ có hai lựa chọn rõ ràng:

1. **Cài vào partition đã chọn**
   - chỉ format/ghi partition Windows được chọn;
   - giữ các partition data khác;
   - tạo hoặc cập nhật boot theo firmware đã xác định.
2. **Xoá sạch disk rồi cài**
   - xoá toàn bộ layout của disk đã chọn;
   - tự tạo GPT/UEFI hoặc MBR/BIOS theo lựa chọn;
   - chỉ bật sau cảnh báo đỏ có model, serial, dung lượng và danh sách partition
     sẽ mất.

Không đưa nâng cấp in-place, migrate Windows đang chạy, dual-boot, VHD boot,
capture WIM hay chuyển MBR↔GPT vào MVP. Những việc đó có phạm vi rollback và
firmware khác, cần plan riêng.

Tùy chọn unattended MVP:

- edition/index;
- tên máy;
- Windows partition, EFI/System partition và Recovery partition;
- tạo một tài khoản local Administrator;
- mật khẩu nhập một lần, chỉ giữ trong RAM;
- keyboard/locale/time zone mặc định FelixPE;
- tự đăng nhập sau cài: mặc định tắt;
- không tự kích hoạt, không ghi product key vào log;
- không tự xoá thư mục Windows.old nếu engine tạo ra.

Mỗi bước có trạng thái `Chưa kiểm`, `Đã kiểm`, `Đang chạy`, `Thất bại`,
`Đã hậu kiểm`. Nút bắt đầu bị khoá nếu source, target, firmware, dung lượng
hoặc layout chưa đạt.

## Cổng an toàn bắt buộc

Trước mọi ghi đĩa:

- từ chối disk/partition đang chứa media FelixPE hoặc thư mục `Apps`;
- từ chối target read-only, BitLocker chưa mở, identity thay đổi, hoặc source
  nằm trên chính target;
- từ chối nếu target là disk đang chạy PE hoặc chứa boot của phiên PE;
- kiểm tra architecture x64 và image index;
- tính dung lượng cần dùng trước khi mở engine;
- đọc lại layout ngay trước apply, so với snapshot đã duyệt;
- hiện chính xác các partition sẽ format/xoá;
- yêu cầu xác nhận riêng cho thao tác xoá sạch disk;
- ghi journal và log ngoài target, trong `%TEMP%\FelixPE\WindowSetup\<run-id>`;
- khi lỗi, giữ log và trạng thái dở; không báo thành công nếu chưa hậu kiểm.

Không hứa rollback toàn bộ sau khi đã format hoặc ghi boot. MVP chỉ rollback
được các bước chuẩn bị chưa ghi và giữ snapshot layout/log để cứu hộ thủ công.

## Tách tầng mã

`WindowSetup.Core` dự kiến có các nhóm:

- `SourceInspection`: đọc WIM/ESD/ISO, enumerate image;
- `TargetInspection`: đọc disk/partition/volume và nhận diện media/boot;
- `InstallPlan`: pure planner cho hai preset, tính dung lượng và thay đổi;
- `SafetyGuards`: overlap, BitLocker, read-only, identity, firmware;
- `Unattend`: tạo XML từ model đã validate, không chứa secret sau khi job kết thúc;
- `WinNtSetupCommand`: builder tham số/working directory từ bảng đã đo;
- `InstallVerification`: hậu kiểm Windows/boot/BCD;
- `InstallJournal`: trạng thái và log, không phụ thuộc WPF.

View chỉ bind model/command; ViewModel điều phối loading, progress, lỗi và
confirmation. Không để XML, diskpart hoặc command line nằm trong code-behind.

## Kế hoạch triển khai theo mốc

### Trạng thái hiện tại

Luồng cơ bản đã có executor: đọc ISO/WIM/ESD, chọn edition, kiểm lại identity
disk và partition, format partition Windows hoặc tạo layout GPT/MBR mới, chạy
WinNTSetup 5.4.2 ở chế độ silent, dựng boot, cấu hình WinRE và hậu kiểm file
Windows, loader, BCD cùng Winre.wim. App sao chép tạm `Autounattend.xml` ra
ngoài target trước khi format, rồi xoá bản tạm khi kết thúc.

Unit test, build WPF, build integration, lint mockup, publish self-contained và
runtime smoke đã chạy. Bài integration VHDX đã xanh bằng token Administrator
ngày 27/10/2026 cho cả GPT/UEFI và MBR/BIOS. Cùng ngày, ca GPT/UEFI chạy trọn
trong FelixPE 26.5.2: ISO Windows 11, xoá đĩa, apply, boot tới desktop, tài
khoản và tên máy từ `Autounattend.xml`, WinRE bật
([biên bản](../specs/2026-10-27-window-setup-uefi-felixpe.md)). MBR/BIOS, cài
vào partition có sẵn và đĩa thật chưa có bằng chứng. Các nhóm Driver, Rescue
USB, Wi-Fi và first-desktop vẫn bị khóa; chưa được coi là chức năng đã triển
khai.

### Mốc 0 — feasibility engine

- Đọc command line/help của WinNTSetup 5.4.2 thực tế trong payload.
- Dùng một VHDX mới cho GPT/UEFI và một VHDX mới cho MBR/BIOS.
- Chứng minh một ca apply WIM + boot bằng command line, thu log/exit code.
- Chứng minh một ca tạo account từ Unattend.
- Nếu không chứng minh được, ghi rõ giới hạn và mở plan backend riêng.

**Đầu ra:** bảng tham số, exit code, log path, yêu cầu runtime và bằng chứng
boot; chưa có UI automatic.

### Mốc 1 — UI shell và model

- Tham chiếu `src/Felix.UI` có sẵn trong repo Partition.
- Tạo `WindowSetup.Core` models/guards/planner.
- Tạo biểu mẫu dọc theo mockup, gồm bộ cài, đích, driver và cấu hình tự động.
- Unit test source/index, identity, overlap, firmware và destructive diff.

**Đầu ra:** app mở được trong Windows/WinPE, chưa ghi đĩa.

### Mốc 2 — installation preflight

- Bundle engine WinNTSetup và asset của nó trong app ngoài WIM.
- Đọc source/target và tạo command context.
- Tạo hoặc chọn `Autounattend.xml` tùy cấu hình.
- Dry-run cả nhánh unattended và nhánh cài Windows bình thường.

**Đầu ra:** một luồng cài duy nhất chạy được preflight; chưa ghi disk thật.

### Mốc 3 — automatic preflight

- Wizard preset, review destructive diff, tạo Unattend tạm.
- Dry-run command builder và log đầy đủ.
- Nút `Bắt đầu cài` chỉ bật khi VHD smoke preflight đạt.

**Đầu ra:** automatic mode chạy dry-run; chưa cho disk thật.

### Mốc 4 — automatic apply trên VHDX

- Apply GPT/UEFI và MBR/BIOS.
- Cài vào partition có sẵn và clean disk.
- Hậu kiểm `Windows`, boot files, BCD, edition, account.
- Fault injection trước ghi, giữa apply và sau tạo boot; giữ journal.

**Đầu ra:** bằng chứng VHDX, ảnh màn hình, log, hash source/WIM.

### Mốc 5 — tích hợp FelixPE

- Manifest đổi tên app thành Window Setup; WinNTSetup không còn shortcut riêng.
- Add-MediaApp hỗ trợ bundle directory + EXE trực tiếp + icon riêng.
- Shortcut desktop/Start Menu mở `WindowSetup.exe`.
- Build ISO và boot VM Secure Boot.

**Đầu ra:** app trong media FelixPE, không cần script/trình duyệt.

### Mốc 6 — nghiệm thu

- VM Gen2 Secure Boot với GPT/UEFI.
- VM Gen1/BIOS nếu vẫn giữ hỗ trợ MBR.
- USB/HDD/SSD rời thật sau khi có phê duyệt riêng.
- Mỗi phép thử ghi model/serial, source hash, target identity, firmware, RAM,
  ảnh, log và kết luận.

## Kiểm thử dự kiến

### Unit không UI

- parse image metadata và chọn index;
- tính layout/space;
- source-target overlap;
- nhận diện media FelixPE;
- chặn disk sai identity;
- XML unattended không có secret sau serialize/log;
- command line quote/path;
- verification result và lỗi từng bước.

### Integration

- WinNTSetup mở với asset đầy đủ;
- normal install: chạy không có answer file;
- unattended install: chạy với `Autounattend.xml`;
- auto: apply trên VHDX GPT/UEFI và MBR/BIOS;
- account/locale/hostname sau boot;
- lỗi giữa apply/boot không bị báo xanh giả;
- app chạy khi không có PowerShell hoặc trình duyệt trong PE.

### Không làm trong MVP

- ghi disk production hoặc ổ đang chứa dữ liệu người dùng mà không có xác nhận
  riêng;
- tự động tải ISO/driver;
- tự tạo product key/license;
- xoá dữ liệu người dùng để “sửa lỗi”;
- hứa rollback sau format/ghi boot.

## Điều kiện bắt đầu implementation

Chỉ bắt đầu viết code sau khi Mốc 0 trả lời được ba câu:

1. WinNTSetup 5.4.2 có command line đủ cho preset automatic nào?
2. Cần bundle chính xác những file/asset nào ngoài `WinNTSetup_x64.exe`?
3. Hậu kiểm tối thiểu nào chứng minh Windows sẽ boot trên cả GPT/UEFI và
   MBR/BIOS mà không dựa vào mã thoát?

Nếu chưa trả lời được, Window Setup chỉ làm manual shell + preflight, không
đưa nút automatic apply vào bản phát hành.
