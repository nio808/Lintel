# SPDX-License-Identifier: GPL-3.0-or-later
# Regenerates app.ico, tray.ico and tray-update.ico next to this script.
# Windows PowerShell 5.1 (uses WPF from the .NET Framework): powershell -NoProfile -File make-icons.ps1 [-Preview <dir>]
# Mark: >= 32 px a black notch pill with two concentric quota arcs (Claude orange outside, Codex violet inside) and
# three level bars; <= 24 px (and the tray) just the ring mark on a black disc, which stays readable at 16 px.
param([string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

$claude = [Windows.Media.Color]::FromRgb(0xD9, 0x8A, 0x66)
$codex = [Windows.Media.Color]::FromRgb(0x9C, 0x89, 0xBF)
$accent = [Windows.Media.Color]::FromRgb(0x7C, 0x9C, 0xFF)
$track = [Windows.Media.Color]::FromRgb(0x2A, 0x2D, 0x36)

function Brush($c) { $b = New-Object Windows.Media.SolidColorBrush $c; $b.Freeze(); $b }

function Arc($dc, [double]$cx, [double]$cy, [double]$r, [double]$thick, [double]$sweep, $color) {
    # Starts at 12 o'clock, clockwise.
    $pen = New-Object Windows.Media.Pen (Brush $color), $thick
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    if ($sweep -ge 359.9) { $dc.DrawEllipse($null, $pen, (New-Object Windows.Point $cx, $cy), $r, $r); return }
    $a = $sweep * [Math]::PI / 180
    $start = New-Object Windows.Point $cx, ($cy - $r)
    $end = New-Object Windows.Point ($cx + $r * [Math]::Sin($a)), ($cy - $r * [Math]::Cos($a))
    $fig = New-Object Windows.Media.PathFigure
    $fig.StartPoint = $start
    $seg = New-Object Windows.Media.ArcSegment $end, (New-Object Windows.Size $r, $r), 0, ($sweep -gt 180), 'Clockwise', $true
    $fig.Segments.Add($seg)
    $geo = New-Object Windows.Media.PathGeometry
    $geo.Figures.Add($fig)
    $dc.DrawGeometry($null, $pen, $geo)
}

function RingMark($dc, [double]$s, [bool]$badge) {
    # Unit design on a 32 grid, scaled.
    $k = $s / 32.0
    $c = 16 * $k
    $disc = New-Object Windows.Media.RadialGradientBrush ([Windows.Media.Color]::FromRgb(0x1C, 0x1E, 0x24)), ([Windows.Media.Color]::FromRgb(0, 0, 0))
    $disc.GradientOrigin = New-Object Windows.Point 0.5, 0.25
    $rim = New-Object Windows.Media.Pen (Brush ([Windows.Media.Color]::FromArgb(0x55, 0xFF, 0xFF, 0xFF))), ([Math]::Max(1, 1 * $k))
    $dc.DrawEllipse($disc, $rim, (New-Object Windows.Point $c, $c), (15.2 * $k), (15.2 * $k))
    Arc $dc $c $c (10.6 * $k) (4.0 * $k) 360 $track
    Arc $dc $c $c (10.6 * $k) (4.0 * $k) 285 $claude
    Arc $dc $c $c (4.9 * $k) (3.4 * $k) 360 $track
    Arc $dc $c $c (4.9 * $k) (3.4 * $k) 210 $codex
    if ($badge) {
        $dc.DrawEllipse((Brush $accent), (New-Object Windows.Media.Pen (Brush ([Windows.Media.Color]::FromRgb(0, 0, 0))), (1.6 * $k)),
            (New-Object Windows.Point (26 * $k), (6 * $k)), (5.4 * $k), (5.4 * $k))
    }
}

function PillMark($dc, [double]$s) {
    # Unit design on a 256 grid, scaled.
    $k = $s / 256.0
    $rect = New-Object Windows.Rect (6 * $k), (62 * $k), (244 * $k), (132 * $k)
    $fill = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.Color]::FromRgb(0x24, 0x26, 0x2D)), ([Windows.Media.Color]::FromRgb(0, 0, 0)), 90
    $rim = New-Object Windows.Media.Pen (Brush ([Windows.Media.Color]::FromArgb(0x66, 0xFF, 0xFF, 0xFF))), ([Math]::Max(1, 3 * $k))
    $dc.DrawRoundedRectangle($fill, $rim, $rect, (66 * $k), (66 * $k))
    $cx = 72 * $k; $cy = 128 * $k
    Arc $dc $cx $cy (40 * $k) (15 * $k) 360 $track
    Arc $dc $cx $cy (40 * $k) (15 * $k) 285 $claude
    Arc $dc $cx $cy (17 * $k) (12 * $k) 360 $track
    Arc $dc $cx $cy (17 * $k) (12 * $k) 210 $codex
    $bar = Brush $accent
    $w = 16 * $k
    foreach ($b in @(@(146, 34), @(178, 58), @(210, 22))) {
        $h = $b[1] * $k
        $dc.DrawRoundedRectangle($bar, $null, (New-Object Windows.Rect ($b[0] * $k), ($cy - $h / 2), $w, $h), ($w / 2), ($w / 2))
    }
}

function Png([int]$size, [scriptblock]$draw) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    & $draw $dc $size
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

function Ico([string]$path, [int[]]$sizes, [scriptblock]$pick) {
    $frames = foreach ($s in $sizes) { , (Png $s (& $pick $s)) }
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $frames[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($f in $frames) { $w.Write($f) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $out.ToArray())
    if ($Preview) {
        New-Item -ItemType Directory -Force $Preview | Out-Null
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            [IO.File]::WriteAllBytes((Join-Path $Preview ("{0}-{1}.png" -f [IO.Path]::GetFileNameWithoutExtension($path), $sizes[$i])), $frames[$i])
        }
    }
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ring = { param($dc, $s) RingMark $dc $s $false }
$ringBadge = { param($dc, $s) RingMark $dc $s $true }
$pill = { param($dc, $s) PillMark $dc $s }
Ico (Join-Path $here 'app.ico') @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256) { param($s) if ($s -le 24) { $ring } else { $pill } }
Ico (Join-Path $here 'tray.ico') @(16, 20, 24, 32, 40, 48, 64) { param($s) $ring }
Ico (Join-Path $here 'tray-update.ico') @(16, 20, 24, 32, 40, 48, 64) { param($s) $ringBadge }
"Icons written to $here"
