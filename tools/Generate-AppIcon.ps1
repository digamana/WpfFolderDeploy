Add-Type -AssemblyName System.Drawing

$outputPath = Join-Path $PSScriptRoot '..\Assets\AppIcon.ico'
$outputDirectory = Split-Path $outputPath -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $size / 256.0
    $background = [System.Drawing.RectangleF]::new(8 * $scale, 8 * $scale, 240 * $scale, 240 * $scale)
    $radius = 44 * $scale
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($background.Left, $background.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($background.Right - $diameter, $background.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($background.Right - $diameter, $background.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($background.Left, $background.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $background,
        [System.Drawing.Color]::FromArgb(20, 120, 148),
        [System.Drawing.Color]::FromArgb(16, 74, 110),
        90.0)
    $graphics.FillPath($backgroundBrush, $path)

    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(104, 211, 145))
    $serverBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(225, 241, 246))

    $serverRect = [System.Drawing.RectangleF]::new(48 * $scale, 142 * $scale, 160 * $scale, 60 * $scale)
    $graphics.FillRectangle($serverBrush, $serverRect)
    $graphics.FillRectangle($accentBrush, 62 * $scale, 158 * $scale, 18 * $scale, 18 * $scale)
    $graphics.FillRectangle($accentBrush, 90 * $scale, 158 * $scale, 18 * $scale, 18 * $scale)
    $graphics.FillRectangle($whiteBrush, 124 * $scale, 158 * $scale, 66 * $scale, 8 * $scale)
    $graphics.FillRectangle($whiteBrush, 124 * $scale, 174 * $scale, 66 * $scale, 8 * $scale)

    $arrow = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(128 * $scale, 44 * $scale),
        [System.Drawing.PointF]::new(184 * $scale, 102 * $scale),
        [System.Drawing.PointF]::new(151 * $scale, 102 * $scale),
        [System.Drawing.PointF]::new(151 * $scale, 143 * $scale),
        [System.Drawing.PointF]::new(105 * $scale, 143 * $scale),
        [System.Drawing.PointF]::new(105 * $scale, 102 * $scale),
        [System.Drawing.PointF]::new(72 * $scale, 102 * $scale)
    )
    $graphics.FillPolygon($whiteBrush, $arrow)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()

    $stream.Dispose()
    $accentBrush.Dispose()
    $whiteBrush.Dispose()
    $serverBrush.Dispose()
    $backgroundBrush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()

    [PSCustomObject]@{ Size = $size; Bytes = $bytes }
}

$fileStream = [System.IO.File]::Create($outputPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dimension = if ($image.Size -eq 256) { [byte]0 } else { [byte]$image.Size }
    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write($image.Bytes)
}

$writer.Dispose()
$fileStream.Dispose()
Write-Output "Generated $outputPath"
