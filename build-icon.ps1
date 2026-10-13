$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$source=[System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'assets\app-icon.png'))
$frames=New-Object 'System.Collections.Generic.List[object]'
try {
    foreach ($size in @(16,20,24,32,40,48,64,96,128,256)) {
        $bitmap=New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        $memory=New-Object System.IO.MemoryStream
        try {
            $graphics.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,0,0,$size,$size)
            $bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add(@{size=$size;data=[byte[]]$memory.ToArray()})
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $stream=[System.IO.File]::Create((Join-Path $PSScriptRoot 'campus.ico'))
    $writer=New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset=6+16*$frames.Count
        foreach ($frame in $frames) {
            $dimension=if ($frame.size -eq 256) {0} else {$frame.size}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.data.Length); $writer.Write([uint32]$offset)
            $offset+=$frame.data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.data) }
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $source.Dispose() }
Write-Output 'Built 10 icon resolutions (16-256 px).'
