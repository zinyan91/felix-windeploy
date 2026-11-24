# Window Setup: cài Windows 11 UEFI/GPT trong FelixPE

Ngày 27/10/2026 chạy Window Setup trong FelixPE 26.5.2 trên VM Hyper-V, cài
Windows 11 Pro từ ISO vào đĩa trắng, boot tới desktop và kiểm tài khoản từ
`Autounattend.xml`. Ca này chỉ phủ GPT/UEFI. MBR/BIOS (VM Gen1) chưa chạy.

## Môi trường

- VM riêng `WindowSetup-e7477a82`, ID `57fcfe40-7f71-4d2d-8f07-1f45c0865d21`,
  Gen 2, 2 vCPU, RAM cố định 6 GiB, Secure Boot tắt, không card mạng, không TPM.
  Không dùng VM `FelixPE-26.5.2`.
- PE ISO `FelixPE_26.5.2.iso`, SHA-256
  `FECC625C65772313049492CBA74CF63D3B0941D0A97C5EC1CDD35CB1193DCE75`.
- Windows ISO `en-us_windows_11_business_editions_version_23h2_updated_oct_2025_x64_dvd_c9a85ebe.iso`,
  7.384.391.680 byte, SHA-256
  `B0B640C6AB179B2498168EEBD569339222BF28EFDF23EBE5C597E9380B57DC8C`.
- Payload VHDX 16 GiB (NTFS `WSPayload`, là `C:` trong PE) chứa bản publish,
  bản sao ISO cùng hash và `Autounattend.xml` thử. Đích là VHDX 64 GiB trắng.
- Answer file thử tạo `felixadmin` trong Administrators, đặt tên máy
  `WS-E7477A82`, ẩn EULA/OEM/online account/Wi-Fi, `ProtectYourPC=3`, không
  tự đăng nhập.

## Luồng đã chạy

1. Mở `WindowSetup.exe` từ payload, Browse chọn ISO.
2. Chọn `5. Windows 11 Pro`, Disk 1 (64 GB), Erase disk, UEFI / GPT, nhập
   đường dẫn `Autounattend.xml`, qua hai hộp xác nhận.
3. Window Setup tạo layout EFI 260 MiB FAT32, MSR 16 MiB, Windows NTFS,
   Recovery 1 GiB (type WinRE, thuộc tính `0x8000000000000001`).
4. WinNTSetup 5.4.2 tự gắn ISO thành `E:`, apply index 5 trong 1 phút 32 giây
   (`WIMHost Return : 0`), nạp answer file, chạy `bcdboot /f UEFI` mã 0.
5. `reagentc /setreimage` thành công. Hậu kiểm của app không thiếu tệp nào;
   journal `Completed`.
6. Đổi boot sang ổ đích. Windows qua specialize/OOBE không cần thao tác, dừng ở
   màn hình khoá "Felix Admin".
7. Đăng nhập bằng mật khẩu trong answer file, tới desktop. Trong guest:
   `hostname` = `WS-E7477A82`, `whoami` = `ws-e7477a82\felixadmin`, có
   `BUILTIN\Administrators`, `EditionID` = `Professional`, build 22631.
   `reagentc /info` (elevated) báo WinRE `Enabled` tại
   `harddisk1\partition4\Recovery\WindowsRE`.

Firmware VM có thêm mục boot `File` (Windows Boot Manager) sau khi chạy `bcdboot`.

## Lỗi tìm thấy và đã sửa

- Chọn ISO trong PE (và trên Desktop) báo "Windows could not identify the
  attached ISO". `OpenVirtualDisk` chỉ xin `ATTACH_RO`, còn
  `GetVirtualDiskPhysicalPath` cần thêm `GET_INFO`, nên trả Win32 5. Đã thêm
  quyền này. Bài `WindowSetup.IntegrationTests -- --iso <đường dẫn>` đỏ trước
  khi sửa, xanh ba lần liên tiếp sau khi sửa và kiểm ISO đã tháo.
- Thanh dưới là `DockPanel` có khối nút đứng cuối, nên nút chạy theo độ dài
  dòng trạng thái. Nhập Autounattend làm nút dịch sang phải, cú bấm Install rơi
  vào Refresh và xoá sạch form không hỏi. Khối nút giờ neo phải trước dòng
  trạng thái.
- Cửa sổ 1120×820 lớn hơn màn hình PE 1024×768, mất thanh tiêu đề và cột nhãn.
  Kích thước và kích thước tối thiểu giờ giới hạn theo `SystemParameters.WorkArea`.
- Bước xoá trong màn hình review in định danh nội bộ
  (`Erase 1|RAW:|68719476736|1||68719476736`). Giờ ghi `Erase Disk 1`; có
  unit test.

Hai sửa giao diện đã kiểm lại trong PE với bản publish mới (hash dll trên payload
khớp bản publish), chưa chạy lại toàn bộ luồng cài.

## Chưa giải quyết

- WinNTSetup ghi `Mounted VHD: 1` cho đĩa SCSI của Hyper-V và bật hộp lỗi
  "VHD's cannot be selected as boot drive" dù có `-silent`. Bấm OK thì cài tiếp
  và Windows boot được. Window Setup đợi tiến trình engine không giới hạn, nên
  không có ai bấm thì lượt cài treo. Trên đĩa thật chưa đo.
- Ổ payload chứa ISO vẫn hiện là đích hợp lệ và được chọn sẵn. Lần thử xoá nó bị
  chặn vì nhỏ hơn 32 GB, nên chưa kiểm cổng chặn nguồn nằm trên đích.
- Hộp xác nhận xoá chỉ có số đĩa và dung lượng; thiếu model và danh sách
  partition sẽ mất như plan yêu cầu. Đĩa không có serial thì hiện "· 64.0 GB".
- `/Get-WimInfo` không kèm `/Index` nên Architecture/Version để trống; preflight
  bỏ qua kiểm x64 khi trường này trống.
- Journal ghi `RecoveryPartitionIdentity: "none"` dù chế độ Erase disk đã tạo
  Recovery.
- Log và journal nằm trên ổ RAM `X:` của PE, mất khi tắt máy nếu không chép ra.

## Bằng chứng

`artifacts/hyperv/FelixPE-26.5.2/validation/window-setup-uefi-e7477a82/`:
log guest (journal, diskpart, engine command, verification, reagentc,
WinNTSetup), 18 ảnh màn hình, answer file đã che mật khẩu, `fixture.json` và
`manifest.json` có SHA-256 từng tệp.
