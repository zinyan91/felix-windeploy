[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare','Start','Screen','Run','Text','Key','Click','Collect','Remove')]
    [string]$Action,
    [Parameter(Mandatory)]
    [string]$Fixture,
    [ValidateSet(1,2)]
    [int]$Generation = 2,
    [string]$Scenario = 'default',
    [string]$Command,
    [string]$Path = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
. (Join-Path $repoRoot 'tests\PartitionRegression\HyperVFixtureConsole.ps1')

$fixturePath = [IO.Path]::GetFullPath($Fixture)
$manifestPath = Join-Path $fixturePath 'fixture.json'
$vmName = "WinDeploy-$([IO.Path]::GetFileName($fixturePath).Substring(0, [Math]::Min(8, [IO.Path]::GetFileName($fixturePath).Length)))"

function Save-Fixture([hashtable]$data) {
    New-Item -ItemType Directory -Force -Path $fixturePath | Out-Null
    $data | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $manifestPath
}

function Read-Fixture() {
    if (!(Test-Path $manifestPath)) { throw "Fixture manifest not found: $manifestPath" }
    Get-Content -Raw $manifestPath | ConvertFrom-Json
}

switch ($Action) {
    'Prepare' {
        New-Item -ItemType Directory -Force -Path $fixturePath | Out-Null
        $files = Get-ChildItem (Join-Path $repoRoot 'felix-windeploy\src\WinDeploy\bin') -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $data = @{
            VmName = $vmName
            Generation = $Generation
            Scenario = $Scenario
            PreparedUtc = [DateTime]::UtcNow.ToString('O')
            AppBinary = if ($files) { $files.FullName } else { $null }
            Status = 'Prepared'
        }
        Save-Fixture $data
        Write-Output $manifestPath
    }
    'Start' {
        $data = Read-Fixture
        $vm = Get-VM -Name $data.VmName -ErrorAction Stop
        Start-VM -Name $vm.Name | Out-Null
        if ($vm.Generation -eq 1) { Confirm-HyperVFixtureDvdBoot (Get-VM -Name $vm.Name) }
    }
    'Screen' {
        $data = Read-Fixture
        $vm = Get-VM -Name $data.VmName -ErrorAction Stop
        $output = if ($Path) { [IO.Path]::GetFullPath($Path) } else { Join-Path $fixturePath "screen-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).png" }
        Save-HyperVFixtureScreen $vm $output
        Write-Output $output
    }
    'Run' {
        if ([string]::IsNullOrWhiteSpace($Command)) { throw 'Run requires -Command.' }
        $data = Read-Fixture
        Send-HyperVFixtureCommand (Get-VM -Name $data.VmName -ErrorAction Stop) $Command
    }
    'Text' {
        if ([string]::IsNullOrWhiteSpace($Command)) { throw 'Text requires -Command.' }
        $data = Read-Fixture
        Send-HyperVFixtureCommand (Get-VM -Name $data.VmName -ErrorAction Stop) $Command
    }
    'Key' { throw 'Key is reserved for a future VM-console key map.' }
    'Click' { throw 'Click is reserved for a future VM-console coordinate map.' }
    'Collect' {
        $data = Read-Fixture
        $vm = Get-VM -Name $data.VmName -ErrorAction Stop
        if ($vm.State -ne 'Off') { Stop-VM -Name $vm.Name -TurnOff -Force }
        $data | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $fixturePath 'manifest.json')
        Write-Output (Join-Path $fixturePath 'manifest.json')
    }
    'Remove' {
        $data = Read-Fixture
        if (Get-VM -Name $data.VmName -ErrorAction SilentlyContinue) {
            Remove-VM -Name $data.VmName -Force
        }
        Write-Output "Removed $($data.VmName)"
    }
}
