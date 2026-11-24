# Window Setup phần 1: khung giao diện, Source và Target

## Vì sao làm

Window Setup đã cài được Windows 11 trên VM UEFI
([biên bản](2026-10-27-window-setup-uefi-felixpe.md)), nhưng giao diện còn là biểu
mẫu tạm: không theme, không bản đồ đĩa, xác nhận bằng `MessageBox`. Người dùng
muốn app đạt độ đầy đủ ngang một ảnh mẫu kiểu AVL Windows Auto Installation:
không cần giống, nhưng mỗi chức năng có trên màn hình phải chạy thật và được
test thật.

Việc đó chia sáu phần, làm và nghiệm thu lần lượt:

1. Khung giao diện, Source, Target (tài liệu này).
2. Tuỳ chọn cài Windows: bỏ đăng nhập tài khoản Microsoft, dọn boot menu, kiểm
   image, sửa Unattend.xml, tự khởi động lại.
3. Boot và recovery: thêm Windows song song vào boot menu, tạo Recovery mới.
4. Driver và mạng: driver Intel RST/mạng, sao lưu driver, xuất Wi-Fi, thư mục
   driver cho Windows và WinPE.
5. Sau cài: phần mềm hỗ trợ từ xa kèm mật khẩu, chạy app khi vào desktop lần đầu.
6. USB cứu hộ và tải ISO.

Phần 1 là nền cho các phần sau. Khi xong, luồng cài hiện có chạy trên giao diện
mới, và các phần sau chỉ thêm nhóm vào khối Options.

## Mục tiêu

- Cửa sổ dựng trên `Felix.UI`: `FeatureWindow`, theme Sáng/Tối của
  `ThemeService`, đổi EN/VI bằng `LanguageManager`, icon riêng của Window Setup.
- Ba khối đánh số: 1 Source, 2 Target, 3 Options. Thanh dưới có nút Start, dòng
  trạng thái, đường dẫn log.
- Source chọn được ISO, WIM, ESD và bộ SWM, có Find Source.
- Target là bản đồ đĩa dạng thẻ với hai chế độ, theo phương án B người dùng đã
  chọn.
- Hộp review riêng thay hai `MessageBox`.
- Luồng cài chạy được và được kiểm end-to-end trên VM cho cả UEFI/GPT và BIOS/MBR,
  ở cả hai chế độ đích.

## Không làm trong phần 1

- Các nhóm tuỳ chọn của phần 2 đến 6. Khối Options chỉ có Unattend.xml.
- Không hiện ô khoá hay chức năng "sắp có". Nhóm nào chưa chạy thật thì không
  xuất hiện.
- Không copy giao diện AVL từng pixel.

Phần 1 thất bại nếu: luồng cài đang chạy được bị hỏng; app cho ghi vào đĩa chứa
FelixPE hoặc đĩa chứa chính bộ cài; app báo thành công khi chưa hậu kiểm; hoặc
cửa sổ tràn màn hình PE 1024×768.

## Thanh tiêu đề và khung

Thanh tiêu đề có icon và tên Window Setup, nút đổi EN/VI, nút Sáng/Tối, thu nhỏ
và đóng. Không có tab About/Help.

Đóng app khi đang chuẩn bị đĩa hoặc đang cài vẫn bị chặn như hiện tại.

Ngôn ngữ mặc định theo Felix Partition (quyết định của người viết, người dùng chưa
chọn). Chuỗi hiển thị nằm trong resource EN/VI của app, không viết cứng trong XAML.

## 1 Source

Dòng Image gồm ô đường dẫn, nút Browse… và nút Find Source. Không có thư mục hay
tên tệp quy ước: khách tự chọn đường dẫn, nên Browse là cách chọn chính.

Định dạng nhận: `.iso`, `.wim`, `.esd`, `.swm`. Với SWM, chọn tệp `.swm` đầu
tiên; app nhận các phần `name2.swm`, `name3.swm`… cùng thư mục là một bộ, báo lỗi
nếu thiếu phần. ISO được gắn chỉ đọc để đọc `sources\install.wim`, `.esd` hoặc
`.swm`.

Find Source quét mọi ổ đang có ký tự, trừ ổ RAM `X:` của PE, tìm bốn định dạng
trên. Không dựa vào tên thư mục. Bỏ qua các thư mục `Windows`, `Program Files`,
`Program Files (x86)`, `$Recycle.Bin`, `System Volume Information` để quét nhanh
(quyết định của người viết). Kết quả hiện trong một hộp: đường dẫn, loại, dung
lượng, số edition. Bộ SWM gộp thành một dòng.

Find Source chỉ giữ bộ cài Windows (quyết định của người viết). Danh sách hiện
ngay theo đường dẫn, loại, dung lượng; số edition điền dần ở nền. ISO không có
`sources\install.wim/.esd/.swm`, và image mà DISM không đọc được edition nào, bị
bỏ khỏi danh sách. `boot.wim`, `winre.wim` và image của FelixPE bị bỏ theo tên
và theo đường dẫn trên media FelixPE. Chọn một dòng rồi bấm Use thì điền
vào ô Image và đọc edition như khi Browse. Đang quét thì có thể huỷ.

Ô Edition ghi index, tên, kiến trúc, bản phát hành và build, ví dụ
`5 · Windows 11 Pro · x64 · 23H2 (22631.6060)`. Hiện app gọi `/Get-WimInfo` không
kèm `/Index` nên kiến trúc và build để trống, preflight bỏ qua kiểm x64. Phần 1
phải đọc từng index để có các trường này, và preflight chặn image không phải x64.
Tên bản (23H2, 24H2…) tra theo bảng build trong mã; build không có trong bảng
thì chỉ hiện số build (quyết định của người viết).

Mỗi dòng có chấm xanh khi hợp lệ, đỏ khi chưa. Không thêm chữ giải thích dưới ô.

## 2 Target

### Bản đồ đĩa

Mỗi đĩa là một hàng. Thẻ đầu hàng ghi số đĩa, model, bus, GPT/MBR và dung lượng.
Các thẻ partition rộng theo tỉ lệ dung lượng (dùng `ProportionalSegmentPanel`),
có tên hoặc ký tự ổ, hệ thống tệp, dung lượng và thanh đã dùng. Khoảng trống chưa
phân vùng cũng là một thẻ.

Đĩa không cài được vẫn hiện nhưng mờ, không bấm được, thẻ đĩa ghi lý do: đang
chạy FelixPE, read-only, offline, dynamic, hoặc chứa tệp app đang dùng. Tệp app
đang dùng gồm image đã chọn, thư mục chứa `WindowSetup.exe` và `_WinNTSetup`, và
answer file. Đổi Source hay answer file thì đánh giá lại. Hiện app ẩn hẳn các đĩa
không hợp lệ và còn liệt kê đĩa chứa bộ cài như đích hợp lệ; cả hai phải đổi.

BitLocker xét theo partition (quyết định của người viết): partition còn khoá thì
mờ, không nhận vai trò nào. Erase disk vẫn cho chọn đĩa có partition BitLocker,
và dòng cảnh báo nêu riêng các partition đó.

Ngoài PE (Windows desktop), app vẫn chỉ cho ghi VHD/VHDX như hiện nay. Đĩa thật
và đĩa đang chạy Windows hiện mờ với lý do "Chỉ ghi trong FelixPE"; nhãn
firmware lấy theo máy đang chạy (quyết định của người viết).

Hàng trên cùng của khối: tiêu đề, nút chọn chế độ, nhãn `Firmware: UEFI` hoặc
`BIOS` (tự nhận theo cách PE đang boot) và nút Refresh. Refresh đọc lại đĩa nhưng
không xoá lựa chọn ở Source hay Options.

### Chế độ Install to partition

Bấm một partition là chọn nó làm Windows. App tự gợi ý EFI/System và Recovery trên
cùng đĩa; các thẻ này có viền và nhãn vai trò. Thẻ đang chọn có các nút nhỏ
Windows, EFI, Recovery để gán lại vai trò.

Quy tắc vai trò (quyết định của người viết):

- Cả ba vai trò phải nằm trên cùng một đĩa. Đĩa không có EFI/System hợp lệ thì
  không cài vào partition được; người dùng chuyển sang Erase disk. Thẻ khoảng
  trống không nhận vai trò ở chế độ này.
- Windows: partition từ 20 GB, không phải EFI/MSR/Recovery. Luôn bị format.
- EFI/System với GPT: partition ESP FAT32 có sẵn. Không format, chỉ ghi thêm
  boot files.
- System với MBR: partition NTFS hoặc FAT32 có sẵn trên đĩa đó, có thể là chính
  partition Windows (đĩa một partition kiểu cũ). Nếu nó chưa active, app đặt
  active và ghi rõ điều đó trong tóm tắt và review.
- Recovery: chỉ partition đã mang type Recovery, hoặc "Không dùng". Partition
  Recovery được chọn sẽ bị format; tóm tắt và review ghi rõ.

Firmware ở chế độ này lấy theo cách PE boot, không đổi được.

Boot menu giữ mục có sẵn (người dùng chọn). Cài vào partition không được làm
mất mục boot của Windows hay hệ khác nằm trên partition được giữ lại; chỉ mục
trỏ tới partition vừa format bị thay bằng Windows mới. Hiện bước boot chạy
`bcdboot … /c` và request luôn bật `ClearBootMenu`; phần 1 phải bỏ hành vi dọn
sạch đó. Tuỳ chọn dọn boot menu thuộc phần 2. Erase disk tạo BCD mới trên đĩa
được xoá; mục boot ở đĩa khác không bị đụng tới.

Dưới bản đồ là ô tóm tắt: Windows (kèm "sẽ được format"), EFI/System, Recovery,
và danh sách partition giữ nguyên. Mỗi dòng có chấm xanh khi hợp lệ.

### Chế độ Erase disk and install

Bấm thẻ đĩa thì cả hàng chuyển sang layout sẽ tạo, viền cam: EFI 260 MB FAT32,
MSR 16 MB, Windows phần còn lại, Recovery 1 GB với GPT; System 550 MB active,
Windows, Recovery với MBR. Đây là layout `InstallDiskLayout` đang tạo; nếu số
liệu đổi thì giao diện phải đọc từ cùng nguồn đó. Có nút chọn UEFI/GPT hoặc
BIOS/MBR, mặc định theo firmware PE. Dòng cảnh báo đỏ ghi số đĩa, model, dung
lượng và các partition sẽ mất.

Đĩa nhỏ hơn 32 GB không chọn được ở chế độ này.

## 3 Options

Chỉ có Unattend.xml: ô đường dẫn và Browse…. Để trống thì cài Windows bình
thường với OOBE. Các phần sau thêm nhóm vào khối này.

Answer file nhận mọi tên `.xml` có gốc `unattend` (quyết định của người viết).
Preflight hiện chỉ nhận tên `Autounattend.xml`; quy tắc đó bỏ đi.

## Start và hộp review

Nút Start chỉ bật khi Source, Target và firmware đều hợp lệ. Bấm Start mở hộp
review dạng bảng: Windows (edition, kiến trúc, build), Source, Disk (số, model,
bus, dung lượng), Windows, EFI/System, Recovery, Keep (partition giữ nguyên),
Setup (bình thường hay Unattend.xml). Dưới bảng là dòng cảnh báo đỏ. Hai nút
Back và Install Windows. Ở chế độ xoá đĩa, nút Install Windows chỉ bật sau khi
gõ số đĩa (quyết định của người viết, thay cho hộp xác nhận thứ hai hiện có).

Không in định danh nội bộ của đĩa ra màn hình.

Bố cục trong lúc cài: dòng trạng thái và thanh tiến trình theo từng bước như
hiện tại; khối Source/Target/Options bị khoá.

## Giữ nguyên từ luồng hiện tại

Lõi `WindowSetup.Core`, `InstallStorageCoordinator`, WinNTSetup làm engine, hậu
kiểm Windows/loader/BCD/WinRE, journal và chặn đóng app khi đang chạy.

Sửa thêm các lỗi đã thấy ở lượt VM 27/10:

- Journal ghi Recovery là `none` dù Erase disk đã tạo Recovery.
- Log và journal nằm trên `X:` của PE. Khi cài xong, thành công hay lỗi, app chép
  thư mục run sang partition Windows vừa cài (thư mục đặt tên do người dựng chọn);
  nếu không ghi được thì giữ nguyên chỗ cũ và báo đường dẫn.
- Trong VM Hyper-V, WinNTSetup coi đĩa là VHD và bật hộp chờ người bấm dù chạy
  `-silent`. App không tự bấm hộp này. Khi engine đang chạy, dòng trạng thái phải
  cho biết đang chờ WinNTSetup để người dùng nhìn sang cửa sổ của nó.

## Ràng buộc

- Chạy trong FelixPE không cần mạng, không PowerShell/WMI. Đọc và ghi đĩa qua lõi
  Felix Partition như hiện tại.
- `src/Felix.UI` là bản dùng chung với Felix Partition. Sửa gì trong đó phải chạy
  `PackUriAudit` và test UI của Felix Partition.
- Cửa sổ vừa màn hình 1024×768 không tràn; `FeatureWindow` đã có sẵn việc co theo
  vùng làm việc.
- Theo luật giao diện của repo: không tooltip, không dòng giải thích phụ, nhãn tự
  đủ nghĩa; lỗi hiện nguyên thông điệp.

## Nghiệm thu

Phần 1 chỉ xong khi có bằng chứng (log, ảnh, hash) cho:

- Unit test lõi xanh; bài integration VHDX và ISO xanh; test UI của Window Setup
  kiểm cửa sổ mở được ở 1024×768 và resource nạp đủ.
- Trên VM Gen2 UEFI, trong FelixPE: một lượt Erase disk có Unattend.xml tới
  desktop, và một lượt Install to partition không Unattend.xml (OOBE thường) giữ
  nguyên dữ liệu partition khác (so checksum tệp mẫu trước và sau). Trước lượt
  này, BCD có thêm một mục trỏ tới partition được giữ; `bcdedit /enum all` sau
  khi cài vẫn còn mục đó.
- Trên VM Gen1 BIOS: một lượt Erase disk BIOS/MBR tới desktop, và một lượt
  Install to partition trên đĩa MBR chỉ có một partition (System trùng Windows).
- Nguồn WIM chọn trực tiếp và nguồn SWM, mỗi loại ít nhất một lượt apply.
- Đĩa chứa bộ cài và đĩa FelixPE hiện mờ, không chọn được.
- Find Source tìm được ISO, WIM, SWM đặt ở thư mục bất kỳ trên payload.

## Để người dựng quyết định

Cấu trúc ViewModel, cách tách control bản đồ đĩa, cách viết test UI, tên thư mục
log trên đĩa đích, thứ tự làm trong phần 1.
