[CmdletBinding()]
param(
    [string]$PeIsoPath = 'D:\FelixPE\.tmp\out\FelixPE_26.5.7\FelixPE_26.5.7.iso',
    [string]$WindowsIsoPath = 'C:\Users\Zinya\Downloads\Compressed\en-us_windows_11_business_editions_version_23h2_updated_oct_2025_x64_dvd_c9a85ebe.iso',
    [string]$VmName = 'WinDeploy-E2E'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HyperVFixtureConsole.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workDir = Join-Path $repoRoot '.tmp\vm-e2e'
$reportDir = Join-Path $repoRoot '.tmp\vm-e2e-report'

if (-not (Test-Path -LiteralPath $PeIsoPath)) { throw "FelixPE ISO not found: $PeIsoPath" }
if (-not (Test-Path -LiteralPath $WindowsIsoPath)) { throw "Windows ISO not found: $WindowsIsoPath" }

# Clean previous leftovers safely
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    $old = Get-VM -Name $VmName
    if ($old.State -ne 'Off') { Stop-VM -Name $VmName -TurnOff -Force }
    Remove-VM -Name $VmName -Force
}
if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
if (Test-Path -LiteralPath $reportDir) { Remove-Item -LiteralPath $reportDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workDir, $reportDir | Out-Null

$publishDir = Join-Path $workDir 'publish'
$payloadVhd = Join-Path $workDir 'payload.vhdx'
$targetVhd = Join-Path $workDir 'target.vhdx'
$vmPath = Join-Path $workDir 'vm'
$unattendPath = Join-Path $workDir 'unattend.xml'

try {
    Write-Host '1. Publishing WinDeploy.IntegrationTests (self-contained win-x64)...'
    $proj = Join-Path $repoRoot 'tests\WinDeploy.IntegrationTests\WinDeploy.IntegrationTests.csproj'
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }

    $wimlibPath = Join-Path $publishDir 'Tools\wimlib\x64\wimlib-imagex.exe'
    if (-not (Test-Path -LiteralPath $wimlibPath)) { throw "Missing wimlib-imagex.exe in publish output: $wimlibPath" }

    Write-Host '2. Generating unattend.xml via UnattendGenerator...'
    & (Join-Path $publishDir 'Felix.WinDeploy.IntegrationTests.exe') --generate-unattend $unattendPath
    if ($LASTEXITCODE -ne 0) { throw "Unattend generation failed with code $LASTEXITCODE" }

    Write-Host '3. Creating payload.vhdx (16 GiB) and target.vhdx (64 GiB)...'
    New-VHD -Path $payloadVhd -SizeBytes 16GB -Dynamic | Out-Null
    New-VHD -Path $targetVhd -SizeBytes 64GB -Dynamic | Out-Null

    $mounted = Mount-VHD -Path $payloadVhd -Passthru
    try {
        $disk = $mounted | Get-Disk
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null
        $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'WSPayload' -Confirm:$false | Out-Null
        $driveRoot = "$($part.DriveLetter):\"
        Write-Host "   Payload mounted at $driveRoot"

        Copy-Item -LiteralPath $publishDir -Destination (Join-Path $driveRoot 'publish') -Recurse -Force
        Copy-Item -LiteralPath $WindowsIsoPath -Destination (Join-Path $driveRoot 'win11.iso') -Force
        Copy-Item -LiteralPath $unattendPath -Destination (Join-Path $driveRoot 'unattend.xml') -Force

        $cmdContent = @(
            '@echo off',
            'set "DRV=%~d0"',
            'mkdir "%DRV%\out" 2>nul',
            '"%DRV%\publish\Felix.WinDeploy.IntegrationTests.exe" --pe-install --iso "%DRV%\win11.iso" --unattend "%DRV%\unattend.xml" --log-dir "%DRV%\out" > "%DRV%\out\stdout.log" 2>&1',
            'echo %ERRORLEVEL% > "%DRV%\out\exitcode.txt"',
            'wpeutil shutdown'
        ) -join "`r`n"
        [System.IO.File]::WriteAllText((Join-Path $driveRoot 'run-install.cmd'), $cmdContent, [System.Text.Encoding]::ASCII)
    }
    finally {
        Dismount-VHD -Path $payloadVhd
    }

    Write-Host '4. Creating Hyper-V Gen2 VM and booting FelixPE...'
    New-VM -Name $VmName -Generation 2 -MemoryStartupBytes 6GB -Path $vmPath | Out-Null
    Set-VM -Name $VmName -ProcessorCount 4 -StaticMemory -AutomaticCheckpointsEnabled $false | Out-Null
    $hddPayload = Add-VMHardDiskDrive -VMName $VmName -Path $payloadVhd -Passthru
    $hddTarget = Add-VMHardDiskDrive -VMName $VmName -Path $targetVhd -Passthru
    $dvd = Add-VMDvdDrive -VMName $VmName -Path $PeIsoPath -Passthru
    Set-VMFirmware -VMName $VmName -EnableSecureBoot Off -FirstBootDevice $dvd | Out-Null

    Start-VM -Name $VmName | Out-Null
    Write-Host '   Waiting 45s for FelixPE desktop...'
    Start-Sleep -Seconds 45
    $vm = Get-VM -Name $VmName
    Save-HyperVFixtureScreen $vm (Join-Path $reportDir '01-pe-desktop.png')

    Write-Host '5. Triggering run-install.cmd inside FelixPE...'
    Send-HyperVFixtureCommand $vm 'cmd /c for %d in (C D E F G H) do @if exist %d:\run-install.cmd start "" %d:\run-install.cmd'
    Start-Sleep -Seconds 5
    Save-HyperVFixtureScreen $vm (Join-Path $reportDir '01b-pe-after-cmd.png')

    $peDeadline = (Get-Date).AddMinutes(12)
    $step = 0
    while ((Get-VM -Name $VmName).State -ne 'Off') {
        if ((Get-Date) -gt $peDeadline) {
            Save-HyperVFixtureScreen (Get-VM -Name $VmName) (Join-Path $reportDir '02-pe-timeout.png')
            throw 'Timed out waiting for FelixPE installation to finish and shut down.'
        }
        Start-Sleep -Seconds 30
        $step++
        $cur = Get-VM -Name $VmName
        if ($cur.State -eq 'Running' -and ($step % 2 -eq 0)) {
            try { Save-HyperVFixtureScreen $cur (Join-Path $reportDir "02-pe-progress-$step.png") } catch {}
        }
    }

    Write-Host '6. FelixPE shut down; collecting logs from payload.vhdx...'
    $peOutReport = Join-Path $reportDir 'pe-out'
    New-Item -ItemType Directory -Force -Path $peOutReport | Out-Null
    $ro = Mount-VHD -Path $payloadVhd -Passthru
    try {
        $pDisk = $ro | Get-Disk
        if ($pDisk.IsOffline) { Set-Disk -Number $pDisk.Number -IsOffline $false }
        $pPart = $pDisk | Get-Partition | Where-Object { $_.Type -eq 'Basic' } | Select-Object -First 1
        if (-not $pPart) { throw "No Basic partition found on payload.vhdx (Disk $($pDisk.Number))" }
        if ($pPart.DriveLetter -notmatch '^[A-Z]$') {
            $pPart | Add-PartitionAccessPath -AssignDriveLetter
            Start-Sleep -Seconds 1
            $pPart = Get-Partition -DiskNumber $pDisk.Number -PartitionNumber $pPart.PartitionNumber
        }
        $driveChar = $pPart.DriveLetter
        if ($driveChar -notmatch '^[A-Z]$') { throw "Failed to assign drive letter to payload.vhdx partition." }
        Write-Host "   Payload collected from ${driveChar}:\"
        $srcOut = "${driveChar}:\out"
        if (Test-Path -LiteralPath $srcOut) {
            Get-ChildItem -LiteralPath $srcOut -Recurse -File | ForEach-Object { Write-Host "   Found in out: $($_.FullName) ($($_.Length)B)" }
            Copy-Item -Path (Join-Path $srcOut '*') -Destination $peOutReport -Recurse -Force
        }
        else {
            $rootEntries = (Get-ChildItem -LiteralPath "${driveChar}:\" -Force | ForEach-Object { $_.Name }) -join ', '
            Write-Warning "Folder $srcOut not found. Root entries on ${driveChar}:\ = [$rootEntries]"
        }
    }
    finally {
        Dismount-VHD -Path $payloadVhd
    }

    if (Test-Path -LiteralPath (Join-Path $peOutReport 'stdout.log')) {
        Write-Host '=== PE stdout.log ==='
        Get-Content -LiteralPath (Join-Path $peOutReport 'stdout.log') | ForEach-Object { Write-Host "  $_" }
    }

    $exitCodeFile = Join-Path $peOutReport 'exitcode.txt'
    if (-not (Test-Path -LiteralPath $exitCodeFile)) {
        throw "PE did not produce exitcode.txt in $peOutReport"
    }
    $peExit = (Get-Content -LiteralPath $exitCodeFile -Raw).Trim()
    Write-Host "   PE install exitcode = $peExit"
    if ($peExit -ne '0') {
        throw "PE installation failed with exit code $peExit. Inspect $peOutReport"
    }

    Write-Host '7. Removing DVD & payload disk; booting installed Windows 11 from target.vhdx...'
    Remove-VMDvdDrive -VMName $VmName -ControllerNumber $dvd.ControllerNumber -ControllerLocation $dvd.ControllerLocation
    Remove-VMHardDiskDrive -VMName $VmName -ControllerType $hddPayload.ControllerType -ControllerNumber $hddPayload.ControllerNumber -ControllerLocation $hddPayload.ControllerLocation
    $bootDrive = Get-VMHardDiskDrive -VMName $VmName
    Set-VMFirmware -VMName $VmName -FirstBootDevice $bootDrive | Out-Null

    Start-VM -Name $VmName | Out-Null
    for ($i = 1; $i -le 10; $i++) {
        Start-Sleep -Seconds 45
        $cur = Get-VM -Name $VmName
        if ($cur.State -eq 'Running') {
            $shot = Join-Path $reportDir ("03-win-boot-{0:D2}.png" -f $i)
            try {
                Save-HyperVFixtureScreen $cur $shot
                Write-Host "   Saved $shot"
            }
            catch {}
        }
    }

    Write-Host "E2E Hyper-V run finished. Evidence saved to $reportDir"
}
finally {
    Write-Host '8. Cleaning up temporary VM and VHDX artifacts...'
    if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
        try {
            if ((Get-VM -Name $VmName).State -ne 'Off') { Stop-VM -Name $VmName -TurnOff -Force }
            Remove-VM -Name $VmName -Force
        }
        catch {
            Write-Warning "Failed to clean VM ${VmName}: $($_.Exception.Message)"
        }
    }
    if (Test-Path -LiteralPath $workDir) {
        try { Remove-Item -LiteralPath $workDir -Recurse -Force }
        catch { Write-Warning "Failed to clean workDir ${workDir}: $($_.Exception.Message)" }
    }
}
