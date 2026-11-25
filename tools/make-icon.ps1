# Draws the app icon (blue rounded square with a white clock) and writes a multi-size src\app.ico.
# Usage: .\tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $pad = [Math]::Max(0.5, $s * 0.04); $w = $s - 2 * $pad; $r = $w * 0.24
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(42, 120, 214))), $path)
    # clock face
    $c = $s / 2.0; $cr = $s * 0.30
    $penW = [Math]::Max(1.2, $s * 0.075)
    $white = [Drawing.Color]::White
    $ring = New-Object Drawing.Pen $white, $penW
    $g.DrawEllipse($ring, $c - $cr, $c - $cr, 2 * $cr, 2 * $cr)
    $hand = New-Object Drawing.Pen $white, $penW
    $hand.StartCap = 'Round'; $hand.EndCap = 'Round'
    $g.DrawLine($hand, $c, $c, $c, $c - $cr * 0.62)          # minute hand (12)
    $g.DrawLine($hand, $c, $c, $c + $cr * 0.48, $c + $cr * 0.18)   # hour hand (~4)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
# ICO container: header, directory entries, PNG payloads
$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $out"
