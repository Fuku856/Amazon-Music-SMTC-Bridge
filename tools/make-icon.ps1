# Draws the app icon: a music note broadcasting - "the now playing info, published".
# Emits PNG logos plus a classic DIB-entry .ico (no PNG-compressed entries, so
# System.Drawing's Icon can always read it back).
param([string]$OutDir)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- rounded tile -----------------------------------------------------
    $r = $S * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($S - $d, 0, $d, $d, 270, 90)
    $path.AddArc($S - $d, $S - $d, $d, $d, 0, 90)
    $path.AddArc(0, $S - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($S, $S)),
        [System.Drawing.Color]::FromArgb(255, 46, 216, 224),
        [System.Drawing.Color]::FromArgb(255, 21, 101, 216))
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

    # --- note head + stem -------------------------------------------------
    $headW = $S * 0.30; $headH = $S * 0.225
    $headCx = $S * 0.355; $headCy = $S * 0.705
    $state = $g.Save()
    $g.TranslateTransform($headCx, $headCy)
    $g.RotateTransform(-22)
    $g.FillEllipse($white, - $headW / 2, - $headH / 2, $headW, $headH)
    $g.Restore($state)

    $stemW = $S * 0.062
    $stemX = $headCx + $headW * 0.44
    $stemTop = $S * 0.235
    $g.FillRectangle($white, $stemX, $stemTop, $stemW, ($headCy - $stemTop))

    # --- broadcast arcs ---------------------------------------------------
    # Two arcs at full size; at tray sizes the outer one collapses into the inner
    # one and just reads as a blob, so only the inner arc is drawn there.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($S * 0.075)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $cx = $stemX + $stemW; $cy = $stemTop + $S * 0.055
    $radii = if ($S -le 24) { @(($S * 0.20)) } else { @(($S * 0.18), ($S * 0.31)) }
    foreach ($rad in $radii) {
        $g.DrawArc($pen, ($cx - $rad), ($cy - $rad), ($rad * 2), ($rad * 2), -52, 104)
    }
    $pen.Dispose(); $white.Dispose(); $g.Dispose()
    return $bmp
}

# --- PNG logos -------------------------------------------------------------
foreach ($spec in @(@{n = 'Square44x44Logo.png'; s = 44 }, @{n = 'Square150x150Logo.png'; s = 150 }, @{n = 'StoreLogo.png'; s = 50 })) {
    $b = New-IconBitmap $spec.s
    $b.Save((Join-Path $OutDir $spec.n), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

# --- preview sheet ---------------------------------------------------------
$sizes = @(256, 64, 48, 32, 16)
$sheetW = [int](($sizes | Measure-Object -Sum).Sum) + ($sizes.Count + 1) * 16
$sheet = New-Object System.Drawing.Bitmap([int]$sheetW, [int]360)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 245, 245, 247))
$sg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 36))), 0, 288, $sheetW, 72)
$x = 16
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $sg.DrawImage($b, $x, 16, $s, $s)
    $sg.DrawImage($b, $x, 310, $s, $s)   # on dark, the way a taskbar shows it
    $b.Dispose()
    $x += $s + 16
}
$sg.Dispose()
$sheet.Save((Join-Path $OutDir 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()

# --- .ico with uncompressed DIB entries ------------------------------------
$icoSizes = @(16, 24, 32, 48, 64, 128)
$entries = @()
foreach ($s in $icoSizes) {
    $bmp = New-IconBitmap $s
    $stride = $s * 4
    $xor = New-Object byte[] ($stride * $s)
    for ($y = 0; $y -lt $s; $y++) {
        for ($x2 = 0; $x2 -lt $s; $x2++) {
            $c = $bmp.GetPixel($x2, ($s - 1 - $y))     # DIB rows are bottom-up
            $o = $y * $stride + $x2 * 4
            $xor[$o] = $c.B; $xor[$o + 1] = $c.G; $xor[$o + 2] = $c.R; $xor[$o + 3] = $c.A
        }
    }
    $maskStride = [int][Math]::Ceiling($s / 32.0) * 4
    $and = New-Object byte[] ($maskStride * $s)       # all zero = fully opaque

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($xor.Length + $and.Length))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    $w.Write($xor); $w.Write($and); $w.Flush()
    $entries += , @{ size = $s; data = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$icoPath = Join-Path $OutDir 'AppIcon.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $bw.Write([byte]$(if ($e.size -ge 256) { 0 } else { $e.size }))
    $bw.Write([byte]$(if ($e.size -ge 256) { 0 } else { $e.size }))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.data.Length); $bw.Write([uint32]$offset)
    $offset += $e.data.Length
}
foreach ($e in $entries) { $bw.Write($e.data) }
$bw.Flush(); $fs.Dispose()

# Prove the runtime can load it back - Program.cs does exactly this.
$check = New-Object System.Drawing.Icon($icoPath)
Write-Output ("AppIcon.ico written, {0} bytes, loads at {1}x{2}" -f (Get-Item $icoPath).Length, $check.Width, $check.Height)
$check.Dispose()
