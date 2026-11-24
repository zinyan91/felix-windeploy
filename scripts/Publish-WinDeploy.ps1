param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $workspace 'src\WinDeploy\WinDeploy.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspace '.tmp\windeploy-publish'
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace '.tmp'))
if (-not $output.StartsWith($temporaryRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside the repository .tmp directory.'
}

if (Test-Path -LiteralPath $output) {
    try {
        Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction Stop
    }
    catch {
        $suffix = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
        $output = Join-Path $temporaryRoot "windeploy-publish-$suffix"
        Write-Warning "The previous publish directory is locked. Publishing to $output instead."
    }
}
New-Item -ItemType Directory -Path $output | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$binary = Join-Path $output 'FelixWinDeploy.exe'
$wimlibExe = Join-Path $output 'Tools\wimlib\x64\wimlib-imagex.exe'
$wimlibDll = Join-Path $output 'Tools\wimlib\x64\libwim-15.dll'
if (-not (Test-Path -LiteralPath $binary)) {
    throw 'FelixWinDeploy.exe was not created.'
}
if (-not (Test-Path -LiteralPath $wimlibExe) -or -not (Test-Path -LiteralPath $wimlibDll)) {
    throw 'wimlib-imagex.exe or libwim-15.dll was not copied to Tools\wimlib\x64.'
}

Get-FileHash -Algorithm SHA256 -LiteralPath $binary
Get-FileHash -Algorithm SHA256 -LiteralPath $wimlibExe
