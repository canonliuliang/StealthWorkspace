param([string]$SourcePath = (Join-Path $PSScriptRoot '..\Assets\AppIcon.Source.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
# Mechanical size conversion only. New-BrandMaster.ps1 regenerates the original vector master.
$source = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($SourcePath))
$frames = @()
try {
    if ($source.Width -ne $source.Height) { throw 'App icon master must be square.' }
    foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
            if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'AppIcon.png'),$memory.ToArray()) }
            if ($size -lt 256) {
                # Small Windows Shell icons use classic 32-bit DIB + AND mask.
                # Retain PNG only for the 256px frame for legacy icon consumers.
                $dib = [IO.MemoryStream]::new()
                $dibWriter = [IO.BinaryWriter]::new($dib)
                try {
                    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
                    $dibWriter.Write([uint32]40)
                    $dibWriter.Write([int32]$size); $dibWriter.Write([int32]($size * 2))
                    $dibWriter.Write([uint16]1); $dibWriter.Write([uint16]32)
                    $dibWriter.Write([uint32]0); $dibWriter.Write([uint32]($size * $size * 4))
                    1..4 | ForEach-Object { $dibWriter.Write([uint32]0) }
                    for ($y = $size - 1; $y -ge 0; $y--) {
                        for ($x = 0; $x -lt $size; $x++) {
                            $pixel = $bitmap.GetPixel($x,$y)
                            $dibWriter.Write([byte]$pixel.B); $dibWriter.Write([byte]$pixel.G)
                            $dibWriter.Write([byte]$pixel.R); $dibWriter.Write([byte]$pixel.A)
                        }
                    }
                    for ($y = $size - 1; $y -ge 0; $y--) {
                        $mask = [byte[]]::new($maskStride)
                        for ($x = 0; $x -lt $size; $x++) {
                            if ($bitmap.GetPixel($x,$y).A -eq 0) {
                                $byteIndex = [int][Math]::Floor($x / 8.0)
                                $mask[$byteIndex] = $mask[$byteIndex] -bor (0x80 -shr ($x % 8))
                            }
                        }
                        $dibWriter.Write($mask)
                    }
                    $frames += @{Size=$size;Bytes=$dib.ToArray()}
                } finally { $dibWriter.Dispose(); $dib.Dispose() }
            } else { $frames += @{Size=$size;Bytes=$memory.ToArray()} }
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
$stream = [IO.File]::Create((Join-Path $assetRoot 'AppIcon.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) {0} else {$frame.Size}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output ('Generated PNG + ICO sizes: ' + ($frames.Size -join ', '))
