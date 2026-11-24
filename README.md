# Felix WinDeploy

Felix WinDeploy là ứng dụng WPF (.NET 10, `win-x64`) dùng để cài đặt Windows, sao lưu ảnh hệ thống và đánh giá tương thích phần cứng trên Windows Desktop lẫn môi trường cứu hộ `FelixPE`. Dự án dùng chung thư viện giao diện `Felix.UI` đặt tại repo `../felix-desktop`.

## Chức năng chính

Ứng dụng chia thành ba phân khu trên cùng một cửa sổ:

1. Cài Windows: đọc danh sách phiên bản trong tệp `.iso`, `.wim`, `.esd` và bộ `.swm` bằng `wimlib-imagex info` (có fallback sang DISM), tự quét bộ cài trên các ổ đĩa đang gắn và lọc bỏ ảnh WinPE/WinRE cùng mảnh `.swm` phụ. Hỗ trợ kiểm tra toàn vẹn ảnh (`wimlib-imagex verify`), đối chiếu mã băm SHA-256, cài vào phân vùng có sẵn hoặc xoá đĩa tạo bố cục mới theo chuẩn `UEFI / GPT` và `BIOS / MBR`. Khi chạy trên Windows Desktop, ứng dụng đóng gói phiên cài và chuyển tiếp khởi động một lần vào `FelixPE` để ghi đĩa. Phần cấu hình triển khai gồm sao lưu driver máy đang chạy, nạp thêm thư mục driver và WinPE phụ, sinh `Unattend.xml` bỏ tài khoản Microsoft, lọc ứng dụng cài sẵn, đặt sẵn phần mềm hỗ trợ từ xa, ghi hình trong WinPE và tạo USB cứu boot.
2. Sao lưu Windows: chụp phân vùng Windows ra tệp `.wim`, `.esd` hoặc `.swm` theo ba chế độ gồm chụp trực tiếp qua VSS/offline, khởi động vào WinPE để chụp ảnh sạch, và chạy Sysprep `/generalize` kèm ảnh dự phòng tự khôi phục khi hoàn tất hoặc khi gặp lỗi.
3. Phần cứng & Đề xuất: đọc thông số CPU (`POPCNT`, `SSE4.2`), RAM, GPU, bo mạch, chuẩn khởi động, `TPM 2.0`, `Secure Boot` và loại ổ đĩa qua Win32 API, IOCTL, TBS và Registry (không gọi WMI hay PowerShell). Từ kết quả đo, ứng dụng chấm 6 hạng mục phần cứng và xếp hạng mức độ phù hợp của 4 bản Windows (`Windows 11 24H2`, `Windows 11 IoT Enterprise LTSC 2024`, `Windows 10 IoT Enterprise LTSC 2021`, `Windows 10 Pro 22H2`).

## Cấu trúc thư mục

- `src/WindowSetup.Core`: mô hình dữ liệu, kiểm tra điều kiện trước khi cài (`WindowSetupPreflight`), sinh `Unattend.xml`, đánh giá phần cứng (`HardwareAdvisor`) và bộ đọc thông tin ảnh.
- `src/WindowSetup.Storage`: đọc bảng phân vùng, gọi DiskPart, gắn ổ ảo và ghi mục khởi động qua Win32/IOCTL.
- `src/WindowSetup`: giao diện WPF, ViewModel và các dịch vụ hệ thống.
- `tools/wimlib/x64`: `wimlib-imagex.exe` (1.14.5) và `libwim-15.dll` đi kèm ứng dụng.
- `tools/felixpe`: kịch bản khởi động tiếp nối phiên cài/sao lưu và menu cứu hộ trong WinPE.
- `tests/WindowSetup.Core.Tests`: unit test cho tầng nghiệp vụ thuần.
- `tests/WindowSetup.UiTests`: kiểm thử ViewModel, `LayoutProbe` (`1160x820`), `PackUriAudit` và kiểm thử `wimlib-imagex` thực tế.
- `tests/WindowSetup.IntegrationTests` và `tests/vm`: kiểm thử bố cục trên VHDX và khung chạy thử Hyper-V.

## Build và kiểm thử

```powershell
dotnet build Felix.WindowSetup.slnx
dotnet run --project tests\WindowSetup.Core.Tests\WindowSetup.Core.Tests.csproj
dotnet run --project tests\WindowSetup.UiTests\WindowSetup.UiTests.csproj
powershell -ExecutionPolicy Bypass -File scripts\Publish-WindowSetup.ps1
```

## Phạm vi chưa tự động hoá

- Bài kiểm thử `WindowSetup.IntegrationTests` tạo VHDX thật cần chạy dưới quyền Administrator kèm đường dẫn tệp ISO Windows.
- Quá trình khởi động thực tế sau cài đặt trên máy ảo Hyper-V (`UEFI / GPT`, `BIOS / MBR` và `WinRE`) cần nghiệm thu thủ công.
