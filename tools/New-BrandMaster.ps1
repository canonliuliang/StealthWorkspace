param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Assets'))
# Original, code-native geometry; matches AppIcon.svg. No external artwork or font.
$bitmap = [Drawing.Bitmap]::new(1024,1024,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [Drawing.Graphics]::FromImage($bitmap)
$plate = [Drawing.Drawing2D.GraphicsPath]::new()
$mark = [Drawing.Drawing2D.GraphicsPath]::new()
$ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#152622'))
$jade = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#91E5CD'))
try {
    $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $plate.AddArc(64,64,416,416,180,90)
    $plate.AddArc(544,64,416,416,270,90)
    $plate.AddArc(544,544,416,416,0,90)
    $plate.AddArc(64,544,416,416,90,90)
    $plate.CloseFigure()
    $g.FillPath($ink,$plate)
    $mark.AddLine(320,224,704,224)
    $mark.AddBezier(704,224,736,224,752,240,752,272)
    $mark.AddLine(752,272,752,368)
    $mark.AddLine(752,368,416,368)
    $mark.AddLine(416,368,416,480)
    $mark.AddLine(416,480,272,480)
    $mark.AddLine(272,480,272,272)
    $mark.AddBezier(272,272,272,240,288,224,320,224)
    $mark.CloseFigure()
    $g.FillPath($jade,$mark)
    $rotation = [Drawing.Drawing2D.Matrix]::new(-1,0,0,-1,1024,1024)
    try { $mark.Transform($rotation) } finally { $rotation.Dispose() }
    $g.FillPath($jade,$mark)
    $bitmap.Save((Join-Path $assetRoot 'AppIcon.Source.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $jade.Dispose(); $ink.Dispose(); $mark.Dispose(); $plate.Dispose(); $g.Dispose(); $bitmap.Dispose() }
& (Join-Path $PSScriptRoot 'New-AppIcon.ps1')
