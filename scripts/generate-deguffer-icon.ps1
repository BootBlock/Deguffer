# Generates every rendering of the Deguffer mark from the one definition below: the multi-size
# ICO, a 256px PNG for the About window, the reusable SVG asset, and the README banner.
#
# The mark is defined once here because it previously lived in the script and in each SVG
# separately, and they drifted — the banner rendered a different part of the gradient from the
# icon it was meant to match. Everything downstream is generated, so that cannot recur.
#
# The gradient matches SpectraWrite's icon generator so the two applications read as a set;
# see New-RainbowBrush for why the axis extends beyond the canvas.

[CmdletBinding()]
param(
    [string]$IcoPath = (Join-Path $PSScriptRoot '..\Deguffer.App\Assets\Deguffer.ico'),
    [string]$PngPath = (Join-Path $PSScriptRoot '..\Deguffer.App\Assets\Deguffer-256.png'),
    [string]$SvgPath = (Join-Path $PSScriptRoot '..\assets\Deguffer.svg'),
    [string]$SmallSvgPath = (Join-Path $PSScriptRoot '..\assets\Deguffer-small.svg'),
    [string]$BannerPath = (Join-Path $PSScriptRoot '..\assets\banner.svg')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# The mark, in a 128-unit space. Boxes of unequal size: accumulated cruft rather than a tidy chart.
#
# Rows are spaced so the strokes clear each other by 5 units. Stroke is centred on the path, so
# each edge eats half the stroke either side — at the original spacing the bottom row's stroke ran
# to 78.25 and the row above started at 77.75, and the two touched at every size.
$script:MarkUnits = 128.0

# Each variant carries its own gradient axis. The axis is fitted to the ink, not the canvas, so a
# variant covering a different area of the canvas needs its own endpoints to land on the same span
# of the ramp — see New-RainbowBrush.
$script:MarkFull = @{
    Stroke = 6.5
    AxisStart = -36.6
    AxisEnd = 177.6
    Boxes = @(
        @{ X = 16; Y = 87.5; W = 96; H = 24; R = 7 }
        @{ X = 28; Y = 52;   W = 42; H = 24; R = 7 }
        @{ X = 78; Y = 52;   W = 28; H = 24; R = 7 }
        @{ X = 28; Y = 16.5; W = 24; H = 24; R = 6 }
    )
}

# Below SmallThreshold the full mark turns to mush: at 16px its rows are ~3px tall, the outlines
# close up and it reads as a coloured blob. This drops to two boxes with a proportionally heavier
# stroke and a wider gap — the same idea at a size that cannot carry the detail.
$script:SmallThreshold = 20
$script:MarkSmall = @{
    Stroke = 12.8
    AxisStart = -49.0
    AxisEnd = 183.9
    Boxes = @(
        @{ X = 16; Y = 76;   W = 96;   H = 37.6; R = 11 }
        @{ X = 16; Y = 14.4; W = 57.6; H = 37.6; R = 11 }
    )
}

# SpectraWrite's stops, as (position, #rrggbb) for the SVG renderings.
$script:MarkStops = @(
    @{ At = '0.00'; Colour = '#FF0000' }
    @{ At = '0.14'; Colour = '#FF8C00' }
    @{ At = '0.28'; Colour = '#FFD700' }
    @{ At = '0.42'; Colour = '#00C853' }
    @{ At = '0.56'; Colour = '#00BCD4' }
    @{ At = '0.70'; Colour = '#2196F3' }
    @{ At = '0.84'; Colour = '#9C27B0' }
    @{ At = '1.00'; Colour = '#FF0000' }
)

function New-RainbowBrush([int]$Size, [hashtable]$Variant)
{
    # Stops and positions are SpectraWrite's. The axis runs beyond the canvas rather than corner
    # to corner: SpectraWrite's glyphs are narrow, so a corner-to-corner ramp leaves their ink
    # spanning only gold #FED401 to blue #2296F1. This mark is wide and would otherwise cover
    # nearly the whole loop, picking up the red and purple that set never shows. The endpoints
    # were fitted per variant by sampling rendered pixels against that reference span.
    $scale = $Size / $script:MarkUnits
    $start = [System.Drawing.PointF]::new($Variant.AxisStart * $scale, $Variant.AxisStart * $scale)
    $end = [System.Drawing.PointF]::new($Variant.AxisEnd * $scale, $Variant.AxisEnd * $scale)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($start, $end, [System.Drawing.Color]::Red, [System.Drawing.Color]::Blue)

    $blend = [System.Drawing.Drawing2D.ColorBlend]::new()
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 255, 0, 0),     # red
        [System.Drawing.Color]::FromArgb(255, 255, 140, 0),   # orange
        [System.Drawing.Color]::FromArgb(255, 255, 215, 0),   # gold
        [System.Drawing.Color]::FromArgb(255, 0, 200, 83),    # green
        [System.Drawing.Color]::FromArgb(255, 0, 188, 212),   # cyan
        [System.Drawing.Color]::FromArgb(255, 33, 150, 243),  # blue
        [System.Drawing.Color]::FromArgb(255, 156, 39, 176),  # purple
        [System.Drawing.Color]::FromArgb(255, 255, 0, 0)      # loop
    )
    $blend.Positions = @(0.0, 0.14, 0.28, 0.42, 0.56, 0.70, 0.84, 1.0)

    $brush.InterpolationColors = $blend
    return $brush
}

function Add-RoundedRect([System.Drawing.Drawing2D.GraphicsPath]$Path, [float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
{
    $d = $R * 2.0
    $Path.StartFigure()
    $Path.AddArc($X, $Y, $d, $d, 180, 90)
    $Path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $Path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $Path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $Path.CloseFigure()
}

function New-MarkBitmap([int]$Size)
{
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try
    {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / $script:MarkUnits
        $variant = if ($Size -le $script:SmallThreshold) { $script:MarkSmall } else { $script:MarkFull }

        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try
        {
            foreach ($box in $variant.Boxes)
            {
                Add-RoundedRect -Path $path `
                    -X ([float]($box.X * $scale)) -Y ([float]($box.Y * $scale)) `
                    -W ([float]($box.W * $scale)) -H ([float]($box.H * $scale)) `
                    -R ([float]($box.R * $scale))
            }

            # A proportional stroke disappears at the smallest sizes, where the outline is the
            # whole design; hold a floor so those frames stay legible rather than ghosting.
            $penWidth = [Math]::Max($variant.Stroke * $scale, 1.4)

            $brush = New-RainbowBrush -Size $Size -Variant $variant
            try
            {
                $pen = [System.Drawing.Pen]::new($brush, [float]$penWidth)
                try
                {
                    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                    $graphics.DrawPath($pen, $path)
                }
                finally
                {
                    $pen.Dispose()
                }
            }
            finally
            {
                $brush.Dispose()
            }
        }
        finally
        {
            $path.Dispose()
        }

        return $bitmap
    }
    catch
    {
        $bitmap.Dispose()
        throw
    }
    finally
    {
        $graphics.Dispose()
    }
}

function Get-PngBytes([System.Drawing.Bitmap]$Bitmap)
{
    $ms = [System.IO.MemoryStream]::new()
    try
    {
        $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    }
    finally
    {
        $ms.Dispose()
    }
}

function Get-GradientStopMarkup([string]$Indent)
{
    ($script:MarkStops | ForEach-Object {
        "$Indent<stop offset=""$($_.At)"" stop-color=""$($_.Colour)""/>"
    }) -join "`n"
}

function Get-BoxMarkup([string]$Indent, [hashtable[]]$Boxes)
{
    ($Boxes | ForEach-Object {
        "$Indent<rect x=""$($_.X)"" y=""$($_.Y)"" width=""$($_.W)"" height=""$($_.H)"" rx=""$($_.R)""/>"
    }) -join "`n"
}

function Write-Svg([string]$Path, [hashtable]$Variant)
{
    # The gradient axis is expressed in the same coordinate space the boxes live in. Where the
    # mark is nested inside a transform (see the banner), userSpaceOnUse resolves in that nested
    # space, so these numbers stay as they are rather than being offset to match.
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
  <title>Deguffer</title>

  <!-- Generated by scripts/generate-deguffer-icon.ps1. Edit the mark there, not here. -->
  <defs>
    <linearGradient id="deguffer-rainbow" gradientUnits="userSpaceOnUse"
                    x1="$($Variant.AxisStart)" y1="$($Variant.AxisStart)" x2="$($Variant.AxisEnd)" y2="$($Variant.AxisEnd)">
$(Get-GradientStopMarkup '      ')
    </linearGradient>
  </defs>

  <g fill="none" stroke="url(#deguffer-rainbow)" stroke-width="$($Variant.Stroke)" stroke-linejoin="round">
$(Get-BoxMarkup '    ' $Variant.Boxes)
  </g>
</svg>
"@

    $dir = Split-Path -Parent $Path
    if ($dir)
    {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $svg)
}

function Write-Banner([string]$Path)
{
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 880 200" width="880" height="200">
  <title>Deguffer</title>

  <!-- Generated by scripts/generate-deguffer-icon.ps1. Edit the mark there, not here. -->
  <defs>
    <linearGradient id="banner-rainbow" gradientUnits="userSpaceOnUse"
                    x1="$($script:MarkFull.AxisStart)" y1="$($script:MarkFull.AxisStart)" x2="$($script:MarkFull.AxisEnd)" y2="$($script:MarkFull.AxisEnd)">
$(Get-GradientStopMarkup '      ')
    </linearGradient>
  </defs>

  <g transform="translate(50,36)">
    <g fill="none" stroke="url(#banner-rainbow)" stroke-width="$($script:MarkFull.Stroke)" stroke-linejoin="round">
$(Get-BoxMarkup '      ' $script:MarkFull.Boxes)
    </g>
  </g>

  <!-- Neutral text: it must stay legible on both GitHub themes, so it carries no gradient. -->
  <text x="216" y="104" font-family="Segoe UI, Helvetica, Arial, sans-serif"
        font-size="54" font-weight="600" fill="#8b95a7">Deguffer</text>
  <text x="219" y="138" font-family="Segoe UI, Helvetica, Arial, sans-serif"
        font-size="18" fill="#6b7484">Reclaim the disk space your toolchain forgot about.</text>
</svg>
"@

    $dir = Split-Path -Parent $Path
    if ($dir)
    {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $svg)
}

function Write-Ico([hashtable[]]$Frames, [string]$Path)
{
    # Frames: @{ Size = 16; Png = [byte[]] }
    $count = $Frames.Count

    $headerSize = 6
    $entrySize = 16
    $offset = $headerSize + ($entrySize * $count)

    $entries = New-Object System.Collections.Generic.List[byte[]]

    foreach ($frame in $Frames)
    {
        $size = [int]$frame.Size
        $pngBytes = [byte[]]$frame.Png

        # 256 is encoded as 0 in the directory; the format has one byte per dimension.
        $dim = if ($size -ge 256) { 0 } else { $size }

        $entry = [byte[]]::new(16)
        $entry[0] = [byte]$dim
        $entry[1] = [byte]$dim
        $entry[2] = 0 # colour count
        $entry[3] = 0 # reserved
        $entry[4] = 1 # planes
        $entry[5] = 0
        $entry[6] = 32 # bit count
        $entry[7] = 0

        [BitConverter]::GetBytes([uint32]$pngBytes.Length).CopyTo($entry, 8)
        [BitConverter]::GetBytes([uint32]$offset).CopyTo($entry, 12)

        $entries.Add($entry)
        $offset += $pngBytes.Length
    }

    $dir = Split-Path -Parent $Path
    if ($dir)
    {
        [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    }

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try
    {
        $fs.Write([byte[]](0,0), 0, 2)  # reserved
        $fs.Write([byte[]](1,0), 0, 2)  # type = 1 (icon)
        $fs.Write([BitConverter]::GetBytes([uint16]$count), 0, 2)

        foreach ($entry in $entries)
        {
            $fs.Write($entry, 0, $entry.Length)
        }

        foreach ($frame in $Frames)
        {
            $pngBytes = [byte[]]$frame.Png
            $fs.Write($pngBytes, 0, $pngBytes.Length)
        }
    }
    finally
    {
        $fs.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = foreach ($s in $sizes)
{
    $bitmap = New-MarkBitmap -Size $s
    try
    {
        @{ Size = $s; Png = (Get-PngBytes -Bitmap $bitmap) }
    }
    finally
    {
        $bitmap.Dispose()
    }
}

Write-Ico -Frames $frames -Path $IcoPath
Write-Host "Wrote icon: $IcoPath" -ForegroundColor Green

$pngDir = Split-Path -Parent $PngPath
if ($pngDir)
{
    [System.IO.Directory]::CreateDirectory($pngDir) | Out-Null
}

$large = New-MarkBitmap -Size 256
try
{
    $large.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally
{
    $large.Dispose()
}

Write-Host "Wrote PNG:  $PngPath" -ForegroundColor Green

Write-Svg -Path $SvgPath -Variant $script:MarkFull
Write-Host "Wrote SVG:  $SvgPath" -ForegroundColor Green

Write-Svg -Path $SmallSvgPath -Variant $script:MarkSmall
Write-Host "Wrote SVG:  $SmallSvgPath (small variant)" -ForegroundColor Green

Write-Banner -Path $BannerPath
Write-Host "Wrote banner: $BannerPath" -ForegroundColor Green
