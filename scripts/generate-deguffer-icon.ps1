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
    [string]$BannerPath = (Join-Path $PSScriptRoot '..\assets\banner.svg')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# The mark, in the 128-unit space of assets/Deguffer.svg. Boxes of unequal size: accumulated
# cruft rather than a tidy chart. Keep the two files in step — this is the same geometry.
$script:MarkUnits = 128.0
$script:MarkStroke = 6.5
$script:MarkBoxes = @(
    @{ X = 16; Y = 81; W = 96; H = 24; R = 7 }
    @{ X = 28; Y = 51; W = 42; H = 24; R = 7 }
    @{ X = 78; Y = 51; W = 28; H = 24; R = 7 }
    @{ X = 28; Y = 23; W = 24; H = 24; R = 6 }
)

# Where the gradient axis starts and ends, in the same 128-unit space. See New-RainbowBrush.
$script:AxisStart = -30.2
$script:AxisEnd = 173.6

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

function New-RainbowBrush([int]$Size)
{
    # Stops and positions are SpectraWrite's. The axis runs from -30.2 to 173.6 in the mark's
    # 128-unit space rather than corner to corner: SpectraWrite's glyphs are narrow, so a
    # corner-to-corner ramp leaves their ink spanning only gold #FED401 to blue #2296F1. This
    # mark is wide and would otherwise cover nearly the whole loop, picking up the red and
    # purple that set never shows. These endpoints were fitted by sampling both icons' pixels.
    $scale = $Size / $script:MarkUnits
    $start = [System.Drawing.PointF]::new($script:AxisStart * $scale, $script:AxisStart * $scale)
    $end = [System.Drawing.PointF]::new($script:AxisEnd * $scale, $script:AxisEnd * $scale)
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

        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try
        {
            foreach ($box in $script:MarkBoxes)
            {
                Add-RoundedRect -Path $path `
                    -X ([float]($box.X * $scale)) -Y ([float]($box.Y * $scale)) `
                    -W ([float]($box.W * $scale)) -H ([float]($box.H * $scale)) `
                    -R ([float]($box.R * $scale))
            }

            # A proportional stroke disappears at 16px, where the outline is the whole design;
            # hold a floor so the smallest frames stay legible rather than ghosting.
            $penWidth = [Math]::Max($script:MarkStroke * $scale, 1.4)

            $brush = New-RainbowBrush -Size $Size
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

function Get-BoxMarkup([string]$Indent)
{
    ($script:MarkBoxes | ForEach-Object {
        "$Indent<rect x=""$($_.X)"" y=""$($_.Y)"" width=""$($_.W)"" height=""$($_.H)"" rx=""$($_.R)""/>"
    }) -join "`n"
}

function Write-Svg([string]$Path)
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
                    x1="$($script:AxisStart)" y1="$($script:AxisStart)" x2="$($script:AxisEnd)" y2="$($script:AxisEnd)">
$(Get-GradientStopMarkup '      ')
    </linearGradient>
  </defs>

  <g fill="none" stroke="url(#deguffer-rainbow)" stroke-width="$($script:MarkStroke)" stroke-linejoin="round">
$(Get-BoxMarkup '    ')
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
                    x1="$($script:AxisStart)" y1="$($script:AxisStart)" x2="$($script:AxisEnd)" y2="$($script:AxisEnd)">
$(Get-GradientStopMarkup '      ')
    </linearGradient>
  </defs>

  <g transform="translate(50,36)">
    <g fill="none" stroke="url(#banner-rainbow)" stroke-width="$($script:MarkStroke)" stroke-linejoin="round">
$(Get-BoxMarkup '      ')
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

Write-Svg -Path $SvgPath
Write-Host "Wrote SVG:  $SvgPath" -ForegroundColor Green

Write-Banner -Path $BannerPath
Write-Host "Wrote banner: $BannerPath" -ForegroundColor Green
