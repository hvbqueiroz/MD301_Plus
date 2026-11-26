[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$PackageAssetDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\src\Assets\MD301Icon.png'))
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $scale = ($size * 0.94) / [Math]::Max($source.Width, $source.Height)
        $width = $source.Width * $scale
        $height = $source.Height * $scale
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, ([System.Drawing.RectangleF]::new(
            (($size - $width) / 2), (($size - $height) / 2), $width, $height)))
    } catch {
        $bitmap.Dispose()
        throw
    } finally {
        $graphics.Dispose()
        $source.Dispose()
    }
    return $bitmap
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    try {
        # DIB frames also work with .NET Framework's Icon.ToBitmap (PNG frames do not).
        if ($size -eq 256) {
            $bitmap.Save((Join-Path $OutputDirectory 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Bmp)
        $bmp = $stream.ToArray()
        $maskLength = [int]([Math]::Ceiling($size / 32.0) * 4 * $size)
        $frame = New-Object byte[] ($bmp.Length - 14 + $maskLength)
        [Array]::Copy($bmp, 14, $frame, 0, $bmp.Length - 14)
        [Array]::Copy([BitConverter]::GetBytes([int]($size * 2)), 0, $frame, 8, 4)
        $frames += ,$frame
    } finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$file = [System.IO.File]::Create((Join-Path $OutputDirectory 'MD301Plus.ico'))
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally {
    $writer.Dispose()
}

if ($PackageAssetDirectory) {
    New-Item -ItemType Directory -Path $PackageAssetDirectory -Force | Out-Null
    $logos = [ordered]@{ 'Square44x44Logo.png' = 44; 'StoreLogo.png' = 50; 'Square150x150Logo.png' = 150 }
    foreach ($logo in $logos.GetEnumerator()) {
        $bitmap = New-IconBitmap $logo.Value
        try {
            $bitmap.Save((Join-Path $PackageAssetDirectory $logo.Key), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $bitmap.Dispose()
        }
    }
}
