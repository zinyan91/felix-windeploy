# Window Setup VM harness

`WindowSetupVm.ps1` giữ các thao tác Hyper-V nguy hiểm ở ngoài ứng dụng. `Prepare`
chỉ tạo manifest; việc gắn ISO/VHDX và tạo VM phải được fixture cụ thể cung cấp
trước khi gọi `Start`. Mỗi lần chạy dùng một thư mục fixture riêng và không chạm
VM `FelixPE-26.5.2`.

Ví dụ:

```powershell
pwsh -File .\window-setup\tests\vm\WindowSetupVm.ps1 `
  -Action Prepare -Fixture .tmp\window-setup-vm-run1 -Generation 2
```

`Screen`, `Run`, `Text` và `Collect` yêu cầu Hyper-V/CIM chạy với quyền phù hợp.
`Key` và `Click` hiện dừng rõ ràng thay vì giả vờ đã điều khiển được VM.
