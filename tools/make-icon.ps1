param([string]$Destination)
Add-Type -AssemblyName System.Drawing
$image=New-Object System.Drawing.Bitmap 64,64
$g=[System.Drawing.Graphics]::FromImage($image)
$g.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$blue=New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(73,108,223))
$white=[System.Drawing.Brushes]::White
$g.Clear([System.Drawing.Color]::FromArgb(73,108,223))
$g.FillEllipse($white,18,20,32,24)
$tail=[System.Drawing.Point[]]@((New-Object System.Drawing.Point 20,32),(New-Object System.Drawing.Point 8,21),(New-Object System.Drawing.Point 8,43))
$g.FillPolygon($white,$tail)
$g.FillEllipse($blue,39,27,4,4)
$handle=$image.GetHicon()
$icon=[System.Drawing.Icon]::FromHandle($handle)
$stream=[System.IO.File]::Create($Destination)
try{$icon.Save($stream)}finally{$stream.Dispose();$icon.Dispose();$g.Dispose();$image.Dispose();$blue.Dispose()}
