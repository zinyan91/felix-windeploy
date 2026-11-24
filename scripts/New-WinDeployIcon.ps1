param(
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\src\WinDeploy\Resources\windeploy.ico'
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath -Parent) | Out-Null

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48, 256)
$pngBlobs = New-Object System.Collections.Generic.List[byte[]]
$primary = [System.Drawing.Color]::FromArgb(255, 47, 128, 237)

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $pad = [Math]::Max(2.0, $size * 0.12)
        $gap = [Math]::Max(1.0, $size * 0.08)
        $tile = ($size - 2.0 * $pad - $gap) / 2.0
        $brush = New-Object System.Drawing.SolidBrush($primary)
        try {
            $g.FillRectangle($brush, [float]$pad, [float]$pad, [float]$tile, [float]$tile)
            $g.FillRectangle($brush, [float]($pad + $tile + $gap), [float]$pad, [float]$tile, [float]$tile)
            $g.FillRectangle($brush, [float]$pad, [float]($pad + $tile + $gap), [float]$tile, [float]$tile)
            $g.FillRectangle($brush, [float]($pad + $tile + $gap), [float]($pad + $tile + $gap), [float]$tile, [float]$tile)
        }
        finally {
            $brush.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBlobs.Add($ms.ToArray())
    $ms.Dispose()
}

$fs = [System.IO.File]::Create($OutputPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $len = $pngBlobs[$i].Length
        $dim = if ($s -eq 256) { [byte]0 } else { [byte]$s }
        $bw.Write($dim)
        $bw.Write($dim)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$len)
        $bw.Write([uint32]$offset)
        $offset += $len
    }

    foreach ($blob in $pngBlobs) {
        $bw.Write($blob)
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Output "Generated $OutputPath"
