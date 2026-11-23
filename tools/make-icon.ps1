# Draws the Retro Todo icon (a green ">_" on a little CRT screen, with a check mark)
# at 16/24/32/48/64/256 px and writes them into src\RetroTodo.ico.
# Each size is drawn separately so the small ones stay crisp.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\src\RetroTodo.ico'

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $green = [System.Drawing.Color]::FromArgb(70, 245, 120)
    $dim   = [System.Drawing.Color]::FromArgb(34, 140, 68)

    # bezel + screen
    $bezel = Rounded 0 0 ($n - 0.5) ($n - 0.5) ([math]::Max(2, $n * 0.2))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 24, 20))), $bezel)
    $in = [math]::Max(1.5, $n * 0.09)
    $screen = Rounded $in $in ($n - 2 * $in) ($n - 2 * $in) ([math]::Max(1.5, $n * 0.13))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(5, 26, 12))), $screen)
    if ($n -ge 32) {
        $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, $green)), ([math]::Max(1, $n / 64))), $screen)
    }

    $w = [math]::Max(1.6, $n * 0.085)
    # soft phosphor glow behind the glyphs (bigger sizes only)
    if ($n -ge 48) {
        $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(55, $green)), ($w * 2.6)
        $glow.StartCap = 'Round'; $glow.EndCap = 'Round'; $glow.LineJoin = 'Round'
        $g.DrawLines($glow, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($n * 0.24), ($n * 0.40)),
            (New-Object System.Drawing.PointF ($n * 0.44), ($n * 0.56)),
            (New-Object System.Drawing.PointF ($n * 0.24), ($n * 0.72))))
        $g.DrawLine($glow, ($n * 0.52), ($n * 0.72), ($n * 0.78), ($n * 0.72))
    }

    $pen = New-Object System.Drawing.Pen $green, $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    # ">"
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($n * 0.24), ($n * 0.40)),
        (New-Object System.Drawing.PointF ($n * 0.44), ($n * 0.56)),
        (New-Object System.Drawing.PointF ($n * 0.24), ($n * 0.72))))
    # "_"
    $g.DrawLine($pen, ($n * 0.52), ($n * 0.72), ($n * 0.78), ($n * 0.72))

    # check mark top-right = "todo"
    if ($n -ge 24) {
        $ck = New-Object System.Drawing.Pen $green, ([math]::Max(1.4, $n * 0.06))
        $ck.StartCap = 'Round'; $ck.EndCap = 'Round'; $ck.LineJoin = 'Round'
        $g.DrawLines($ck, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($n * 0.56), ($n * 0.27)),
            (New-Object System.Drawing.PointF ($n * 0.64), ($n * 0.35)),
            (New-Object System.Drawing.PointF ($n * 0.78), ($n * 0.20))))
    }

    # scanlines on the big sizes
    if ($n -ge 64) {
        $sl = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(45, 0, 0, 0)), 1
        $g.SetClip($screen)
        for ($y = $in; $y -lt $n - $in; $y += 3) { $g.DrawLine($sl, 0, $y, $n, $y) }
        $g.ResetClip()
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($n -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)   # big size as PNG (standard)
    } else {
        # small sizes as classic 32-bit DIB: header, bottom-up BGRA pixels, 1-bit AND mask
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$n); $bw.Write([Int32]($n * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($n * $n * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $n - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $n; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($n / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $n)))   # all zero: alpha channel decides
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (Draw $s) }

# ICO container with PNG-compressed entries (supported since Windows Vista)
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "> wrote $out" -ForegroundColor Green
