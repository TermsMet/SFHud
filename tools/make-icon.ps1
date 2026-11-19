<#
.SYNOPSIS
    Builds SFHud's multi-size Windows icon (.ico) and the in-app logo PNG from assets/icon.png.
.DESCRIPTION
    Frames at 16, 20, 24, 32, 40, 48 and 64 px are classic 32-bit BMP (DIB) frames, which every
    Windows API reads. The 128 and 256 px frames are PNG-compressed to keep the file small.
    Also writes a 256 px PNG that the app shows in its title bar.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\icon.png'),
    [string]$IcoOut = (Join-Path $PSScriptRoot '..\src\SFHud.App\Assets\sfhud.ico'),
    [string]$PngOut = (Join-Path $PSScriptRoot '..\src\SFHud.App\Assets\sfhud-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
New-Item -ItemType Directory -Force -Path (Split-Path $IcoOut) | Out-Null

function Get-Resized([System.Drawing.Image]$img, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.DrawImage($img, 0, 0, $size, $size)
    } finally { $g.Dispose() }
    return $bmp
}

function ConvertTo-Png([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bpp AND mask (height is doubled per the ICO format).
function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($s * $s * 4 + $maskStride * $s))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $s * 4) }
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $s; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -lt 128) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
        $w.Write($row)
    }
    $w.Flush()
    return ,$ms.ToArray()
}

$img = [System.Drawing.Image]::FromFile($Source)
try {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $frames = @()
    $png256 = $null
    foreach ($s in $sizes) {
        $bmp = Get-Resized $img $s
        try {
            if ($s -ge 128) {
                $bytes = ConvertTo-Png $bmp
                if ($s -eq 256) { $png256 = $bytes }
            } else {
                $bytes = ConvertTo-Dib $bmp
            }
            $frames += ,$bytes
        } finally { $bmp.Dispose() }
    }

    $fs = [System.IO.File]::Create($IcoOut)
    $w = New-Object System.IO.BinaryWriter $fs
    try {
        $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $s = $sizes[$i]; $len = $frames[$i].Length
            $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256
            $w.Write([byte]$dim); $w.Write([byte]$dim)
            $w.Write([byte]0); $w.Write([byte]0)       # palette, reserved
            $w.Write([UInt16]1); $w.Write([UInt16]32)  # planes, bpp
            $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
            $offset += $len
        }
        foreach ($f in $frames) { $w.Write($f) }
    } finally { $w.Dispose(); $fs.Dispose() }

    [System.IO.File]::WriteAllBytes($PngOut, $png256)
} finally { $img.Dispose() }

Write-Host "Icon:  $IcoOut ($($sizes -join ', ') px)"
Write-Host "Logo:  $PngOut"
