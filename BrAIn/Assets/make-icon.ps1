Add-Type -AssemblyName System.Drawing
$s = 256
$bmp = New-Object System.Drawing.Bitmap $s, $s
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'

$rect = New-Object System.Drawing.Rectangle 8, 8, 240, 240
$r = 56
$d = $r * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
$path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
$path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
    ([System.Drawing.Color]::FromArgb(110, 110, 255)), `
    ([System.Drawing.Color]::FromArgb(60, 180, 160)), 45
$g.FillPath($brush, $path)

$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(96, 40, 120, 120)
$pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$pb.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
$pb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$g.FillPath($pb, $glow)

$font = New-Object System.Drawing.Font('Segoe UI', 132, [System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'
$sf.LineAlignment = 'Center'
$g.DrawString('B', $font, [System.Drawing.Brushes]::White, 128, 140, $sf)

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
$w.Write($png)
$w.Flush()

$out = Join-Path $PSScriptRoot 'icon.ico'
[System.IO.File]::WriteAllBytes($out, $ico.ToArray())
Write-Output ("icon.ico bytes=" + $ico.Length)
