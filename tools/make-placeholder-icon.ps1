<#
  Generates a placeholder pkg\Assets\AppIcon.ico matching the music-note MSIX tiles.

  Replace that file with a real icon whenever one is available - the app loads it
  by path at runtime, so nothing here needs changing.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repo 'pkg\Assets\AppIcon.ico'
$sizes  = @(16, 24, 32, 48, 64, 128, 256)

$background = [System.Drawing.Color]::FromArgb(255, 26, 32, 44)
$foreground = [System.Drawing.Color]::FromArgb(255, 37, 209, 218)

$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    $brushBg = New-Object System.Drawing.SolidBrush($background)
    $g.FillRectangle($brushBg, 0, 0, $size, $size)

    $brushFg = New-Object System.Drawing.SolidBrush($foreground)
    $font = New-Object System.Drawing.Font('Segoe UI Symbol', [float]($size * 0.55))
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString([char]0x266A, $font, $brushFg, $rect, $format)

    $g.Dispose()

    # Vista-era .ico files may hold PNG frames directly, which keeps 256x256 small.
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$frames.Count)

# ICONDIRENTRY table, then the payloads.
$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)   # width
    $writer.Write([byte]$dimension)   # height
    $writer.Write([byte]0)            # palette size
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # colour planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$frame.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose()

Write-Host "wrote $target ($((Get-Item $target).Length) bytes, $($frames.Count) sizes)" -ForegroundColor Green
