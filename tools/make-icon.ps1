<#
  Generates src/Sts2Overlay.App/Assets/Sts2Overlay.ico: our own gold "S" tier badge (no game art).
  Each size (16/32/48/256) is drawn separately so the small ones stay crisp, and stored as a PNG frame
  (Windows Vista+ reads PNG frames in .ico files at any size).

    pwsh -File tools/make-icon.ps1        (Windows PowerShell 5.1 works too)
#>
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Sts2Overlay.App/Assets/Sts2Overlay.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile with a 1/16 margin.
    $m = [Math]::Max(1.0, $size / 16.0)
    $w = $size - 2 * $m
    $r = $w * 0.28
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90)
    $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90)
    $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()

    # Gold gradient (light top → deeper bottom), dark outline.
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 252, 219, 92)), ([System.Drawing.Color]::FromArgb(255, 214, 158, 8)), 90.0
    $g.FillPath($fill, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 74, 52, 6)), ([Math]::Max(1.0, $size / 24.0))
    $g.DrawPath($pen, $path)

    # Bold "S", centred on its glyph bounds (not the font's line box).
    $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
    $family = New-Object System.Drawing.FontFamily 'Segoe UI'
    $glyph.AddString('S', $family, [int][System.Drawing.FontStyle]::Bold, $size * 0.78, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $glyph.GetBounds()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate(($size - $b.Width) / 2 - $b.X, ($size - $b.Height) / 2 - $b.Y)
    $glyph.Transform($matrix)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 24, 14))), $glyph)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 32, 48, 256
$frames = @($sizes | ForEach-Object { , (New-Frame $_) })

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
# ICONDIR
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }  # 0 means 256
    # ICONDIRENTRY: width, height, colours, reserved, planes, bpp, bytes, offset
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f) }
$bw.Dispose()
Write-Host ("wrote {0} ({1:N0} bytes, sizes {2})" -f $Out, (Get-Item $Out).Length, ($sizes -join '/'))
