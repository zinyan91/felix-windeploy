function Confirm-HyperVFixtureDvdBoot($Vm) {
    if ($Vm.Generation -ne 1) { return }
    if ($Vm.State -ne 'Running') { throw 'Fixture VM is not running.' }
    $keyboard = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_Keyboard |
        Where-Object SystemName -eq $Vm.Id.ToString()
    if (!$keyboard) { throw 'Fixture keyboard unavailable.' }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $result = Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32]32 }
        if ($result.ReturnValue -ne 0) { throw "Fixture DVD boot key failed: $($result.ReturnValue)" }
        Start-Sleep -Milliseconds 500
    }
}

function Save-HyperVFixtureScreen($Vm, [string]$Path) {
    if ($Vm.State -ne 'Running') { throw 'Fixture VM is not running.' }
    $width = if ($Vm.Generation -eq 1) { 640 } else { 1024 }
    $height = if ($Vm.Generation -eq 1) { 480 } else { 768 }
    $ns = 'root/virtualization/v2'
    $service = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
    $setting = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemSettingData `
        -Filter "ConfigurationID='$($Vm.Id)'" | Select-Object -First 1
    $image = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{
        TargetSystem = $setting; WidthPixels = [uint16]$width; HeightPixels = [uint16]$height
    }
    if ($image.ReturnValue -ne 0 -or $image.ImageData.Length -lt $width * $height * 2) {
        throw "Fixture screenshot failed: $($image.ReturnValue)"
    }
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $width, $height),
        [Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
    try {
        for ($row = 0; $row -lt $height; $row++) {
            [Runtime.InteropServices.Marshal]::Copy($image.ImageData, $row * $width * 2,
                [IntPtr]::Add($data.Scan0, $row * $data.Stride), $width * 2)
        }
    }
    finally { $bitmap.UnlockBits($data) }
    try { $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

function Send-HyperVFixtureCommand($Vm, [string]$Command) {
    if ($Vm.State -ne 'Running' -or $Command.Length -gt 250) { throw 'Invalid fixture console command.' }
    $keyboard = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_Keyboard |
        Where-Object SystemName -eq $Vm.Id.ToString()
    if (!$keyboard) { throw 'Fixture keyboard unavailable.' }
    function Key([string]$method, [uint32]$code) {
        $result = Invoke-CimMethod -InputObject $keyboard -MethodName $method -Arguments @{ keyCode = $code }
        if ($result.ReturnValue -ne 0) { throw "Fixture key failed: $($result.ReturnValue)" }
    }
    Key PressKey 91
    Key TypeKey 82
    Key ReleaseKey 91
    Start-Sleep -Seconds 1
    Key PressKey 17
    Key TypeKey 65
    Key ReleaseKey 17
    for ($offset = 0; $offset -lt $Command.Length; $offset += 24) {
        $segment = $Command.Substring($offset, [Math]::Min(24, $Command.Length - $offset))
        $result = Invoke-CimMethod -InputObject $keyboard -MethodName TypeText -Arguments @{ asciiText = $segment }
        if ($result.ReturnValue -ne 0) { throw "Fixture text failed: $($result.ReturnValue)" }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Seconds 2
    Key TypeKey 13
}
