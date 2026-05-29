# Generates the ZyncMaster app icon PNG (rounded tile + two-arrow sync glyph in the
# brand azure/terracotta). Output: a 256x256 PNG used for the window + tray icon.
# Usage: powershell -NoProfile -File make-icon.ps1 <output.png>
param([string]$Out)
Add-Type -AssemblyName System.Drawing
$sz = 256
$bmp = New-Object System.Drawing.Bitmap($sz, $sz)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# --- rounded tile with a deep slate-blue gradient ---
$pad = 16; $d = 64
$x = $pad; $y = $pad; $w = $sz - 2*$pad; $h = $sz - 2*$pad
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($x, $y, $d, $d, 180, 90)
$path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
$path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
$path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
$path.CloseFigure()
$rect = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
$top = [System.Drawing.Color]::FromArgb(255, 28, 34, 54)   # #1c2236
$bot = [System.Drawing.Color]::FromArgb(255, 12, 16, 26)   # #0c101a
$tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
$g.FillPath($tile, $path)
# subtle top rim highlight
$rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), 2)
$g.DrawPath($rim, $path)

# --- two-arrow sync glyph: top arc azure, bottom arc terracotta ---
$cx = 128; $cy = 130; $r = 52
$arc = New-Object System.Drawing.Rectangle(($cx - $r), ($cy - $r), (2*$r), (2*$r))
$azure = [System.Drawing.Color]::FromArgb(255, 91, 140, 255)   # #5b8cff
$terra = [System.Drawing.Color]::FromArgb(255, 217, 119, 87)   # #d97757
$penA = New-Object System.Drawing.Pen($azure, 18); $penA.StartCap = 'Round'; $penA.EndCap = 'Flat'
$penT = New-Object System.Drawing.Pen($terra, 18); $penT.StartCap = 'Round'; $penT.EndCap = 'Flat'
$g.DrawArc($penA, $arc, 165, 150)   # top arc, sweeps to the right
$g.DrawArc($penT, $arc, 345, 150)   # bottom arc, sweeps to the left

# arrowheads (filled triangles) at each arc's leading end
function Arrow($brush, $angleDeg, $dir) {
  $a = $angleDeg * [Math]::PI / 180
  $px = $cx + $r * [Math]::Cos($a)
  $py = $cy + $r * [Math]::Sin($a)
  # tangent direction (clockwise) and outward normal
  $tx = -[Math]::Sin($a) * $dir; $ty = [Math]::Cos($a) * $dir
  $nx = [Math]::Cos($a); $ny = [Math]::Sin($a)
  $L = 22; $Wd = 16
  $p1 = New-Object System.Drawing.PointF([single]($px + $tx*$L), [single]($py + $ty*$L))
  $p2 = New-Object System.Drawing.PointF([single]($px + $nx*$Wd - $tx*2), [single]($py + $ny*$Wd - $ty*2))
  $p3 = New-Object System.Drawing.PointF([single]($px - $nx*$Wd - $tx*2), [single]($py - $ny*$Wd - $ty*2))
  $g.FillPolygon($brush, @($p1,$p2,$p3))
}
$brA = New-Object System.Drawing.SolidBrush($azure)
$brT = New-Object System.Drawing.SolidBrush($terra)
Arrow $brA 315 1     # end of top azure arc (165+150)
Arrow $brT 135 1     # end of bottom terra arc (345+150=495=135)

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "SAVED $Out"
