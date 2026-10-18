param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'src/NetPulse/Assets'
$previewDirectory = Join-Path $projectRoot 'docs/assets'
$null = New-Item -ItemType Directory -Path $previewDirectory -Force
$culture = [Globalization.CultureInfo]::InvariantCulture

# The same WPF drawing supplies the app header, portable SVG and all ICO frames.
$stream = [IO.File]::OpenRead((Join-Path $assetDirectory 'NetPulse.Icon.xaml'))
try { $resources = [Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$artwork = $resources['NetPulseIcon'].Drawing

function Write-Png([Windows.Media.Imaging.BitmapSource]$Bitmap, [string]$Path) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $output = [IO.File]::Create($Path)
    try { $encoder.Save($output) } finally { $output.Dispose() }
}

function Get-IconBitmap([int]$Size) {
    $drawing = $artwork.Clone()
    if ($Size -le 32) {
        # Slightly heavier strokes survive the taskbar and title-bar raster grids.
        $drawing.Children[2].Pen.Thickness = 20
        $drawing.Children[3].Pen.Thickness = 18
    }
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($Size / 256.0, $Size / 256.0))
    $context.DrawDrawing($drawing)
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

# Use conventional 32-bit DIB entries up to 128 px, plus PNG at 256 px.
# DIB rows are bottom-up BGRA, followed by a 1-bit AND transparency mask.
function Get-IconDib([Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $size = $Bitmap.PixelWidth
    $straight = [Windows.Media.Imaging.FormatConvertedBitmap]::new($Bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $memory = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($memory)
    try {
        $writer.Write([int]40)
        $writer.Write([int]$size)
        $writer.Write([int]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([int]0)
        $writer.Write([int](($stride + $maskStride) * $size))
        foreach ($unused in 1..4) { $writer.Write([int]0) }
        for ($y = $size - 1; $y -ge 0; $y--) { $writer.Write($pixels, $y * $stride, $stride) }
        for ($y = $size - 1; $y -ge 0; $y--) {
            $mask = [byte[]]::new($maskStride)
            for ($x = 0; $x -lt $size; $x++) {
                if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                    $byteIndex = [int][Math]::Floor($x / 8)
                    $mask[$byteIndex] = $mask[$byteIndex] -bor (128 -shr ($x % 8))
                }
            }
            $writer.Write($mask)
        }
        $writer.Flush()
        return ,$memory.ToArray()
    } finally { $writer.Dispose(); $memory.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @{}
$payloads = foreach ($size in $sizes) {
    $frames[$size] = Get-IconBitmap $size
    if ($size -eq 256) {
        $memory = [IO.MemoryStream]::new()
        try {
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($frames[$size]))
            $encoder.Save($memory)
            ,$memory.ToArray()
        } finally { $memory.Dispose() }
    } else { ,(Get-IconDib $frames[$size]) }
}
$iconPath = Join-Path $assetDirectory 'NetPulse.App.ico'
$iconStream = [IO.File]::Create($iconPath)
$iconWriter = [IO.BinaryWriter]::new($iconStream)
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$payloads[$i].Length)
        $iconWriter.Write([uint32]$offset)
        $offset += $payloads[$i].Length
    }
    foreach ($payload in $payloads) { $iconWriter.Write([byte[]]$payload) }
} finally { $iconWriter.Dispose(); $iconStream.Dispose() }
Write-Png (Get-IconBitmap 1024) (Join-Path $assetDirectory 'NetPulse.App.png')

# Export the simple native geometry to SVG without an external renderer.
$svgDefinitions = [Collections.Generic.List[string]]::new()
function Get-SvgPaint([Windows.Media.Brush]$Brush, [string]$Attribute) {
    if ($null -eq $Brush) { return "$Attribute=`"none`"" }
    if ($Brush -is [Windows.Media.SolidColorBrush]) {
        $color = $Brush.Color
        $hex = '#{0:X2}{1:X2}{2:X2}' -f $color.R, $color.G, $color.B
        $alpha = ($color.A / 255.0).ToString('0.####', $culture)
        return "$Attribute=`"$hex`" $Attribute-opacity=`"$alpha`""
    }
    if ($Brush -is [Windows.Media.LinearGradientBrush]) {
        $id = 'paint' + $svgDefinitions.Count
        $stops = foreach ($stop in $Brush.GradientStops) {
            $color = $stop.Color
            $hex = '#{0:X2}{1:X2}{2:X2}' -f $color.R, $color.G, $color.B
            $alpha = ($color.A / 255.0).ToString('0.####', $culture)
            '<stop offset="{0}" stop-color="{1}" stop-opacity="{2}" />' -f $stop.Offset.ToString($culture), $hex, $alpha
        }
        $gradient = '<linearGradient id="{0}" x1="{1}" y1="{2}" x2="{3}" y2="{4}">{5}</linearGradient>' -f $id, $Brush.StartPoint.X.ToString($culture), $Brush.StartPoint.Y.ToString($culture), $Brush.EndPoint.X.ToString($culture), $Brush.EndPoint.Y.ToString($culture), ($stops -join '')
        $svgDefinitions.Add($gradient)
        return "$Attribute=`"url(#$id)`""
    }
    throw "Unsupported SVG paint: $($Brush.GetType())"
}
$svgShapes = foreach ($child in $artwork.Children) {
    $fill = Get-SvgPaint $child.Brush 'fill'
    $stroke = ''
    if ($null -ne $child.Pen) {
        $paint = Get-SvgPaint $child.Pen.Brush 'stroke'
        $width = $child.Pen.Thickness.ToString($culture)
        $cap = $child.Pen.StartLineCap.ToString().ToLowerInvariant()
        if ($cap -eq 'flat') { $cap = 'butt' }
        $join = $child.Pen.LineJoin.ToString().ToLowerInvariant()
        $stroke = "$paint stroke-width=`"$width`" stroke-linecap=`"$cap`" stroke-linejoin=`"$join`""
    }
    if ($child.Geometry -is [Windows.Media.RectangleGeometry]) {
        $rect = $child.Geometry.Rect
        '<rect x="{0}" y="{1}" width="{2}" height="{3}" rx="{4}" {5} {6} />' -f $rect.X.ToString($culture), $rect.Y.ToString($culture), $rect.Width.ToString($culture), $rect.Height.ToString($culture), $child.Geometry.RadiusX.ToString($culture), $fill, $stroke
    } else {
        $path = $child.Geometry.ToString($culture) -replace '^F[01]\s*', ''
        '<path d="{0}" {1} {2} />' -f $path, $fill, $stroke
    }
}
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-label="NetPulse 网络脉搏">
  <title>NetPulse 网络脉搏</title>
  <defs>$($svgDefinitions -join '')</defs>
  $($svgShapes -join "`n  ")
</svg>
"@
[IO.File]::WriteAllText((Join-Path $assetDirectory 'NetPulse.App.svg'), $svg, [Text.UTF8Encoding]::new($false))

# A review sheet includes real pixel sizes on dark and light Windows surfaces.
function Get-Brush([string]$Color) { return [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($Color) }
function Add-Label($Context, [string]$Text, [double]$X, [double]$Y, [double]$Size, [string]$Color, [bool]$Bold = $false) {
    $weight = if ($Bold) { [Windows.FontWeights]::SemiBold } else { [Windows.FontWeights]::Normal }
    $typeface = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI, Microsoft YaHei UI'), [Windows.FontStyles]::Normal, $weight, [Windows.FontStretches]::Normal)
    $label = [Windows.Media.FormattedText]::new($Text, $culture, [Windows.FlowDirection]::LeftToRight, $typeface, $Size, (Get-Brush $Color), 1.0)
    $Context.DrawText($label, [Windows.Point]::new($X, $Y))
}
$preview = [Windows.Media.DrawingVisual]::new()
$context = $preview.RenderOpen()
$context.DrawRectangle((Get-Brush '#0B1220'), $null, [Windows.Rect]::new(0, 0, 1120, 820))
$context.DrawImage((Get-IconBitmap 512), [Windows.Rect]::new(54, 44, 296, 296))
Add-Label $context 'NetPulse' 408 107 62 '#F0F7FF' $true
Add-Label $context '网络脉搏' 412 189 27 '#91AAC8'
Add-Label $context '测速弧线 × 网络脉冲' 412 248 19 '#65DEBD'
Add-Label $context 'WINDOWS APP ICON' 60 372 13 '#7892B2' $true
$context.DrawRoundedRectangle((Get-Brush '#131F31'), $null, [Windows.Rect]::new(52, 410, 1016, 170), 18, 18)
$context.DrawRoundedRectangle((Get-Brush '#F1F5FA'), $null, [Windows.Rect]::new(52, 604, 1016, 170), 18, 18)
$sampleSizes = @(16, 20, 24, 32, 48, 64, 128)
for ($row = 0; $row -lt 2; $row++) {
    $top = 410 + 194 * $row
    $labelColor = if ($row -eq 0) { '#91AAC8' } else { '#53667E' }
    Add-Label $context $(if ($row -eq 0) { '深色' } else { '浅色' }) 76 ($top + 61) 15 $labelColor
    for ($i = 0; $i -lt $sampleSizes.Count; $i++) {
        $size = $sampleSizes[$i]
        $center = 205 + $i * 119
        $context.DrawImage($frames[$size], [Windows.Rect]::new($center - $size / 2.0, $top + 4 + (136 - $size) / 2.0, $size, $size))
        Add-Label $context ([string]$size + ' px') ($center - 17) ($top + 147) 12 $labelColor
    }
}
$context.Close()
$sheet = [Windows.Media.Imaging.RenderTargetBitmap]::new(1120, 820, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$sheet.Render($preview)
Write-Png $sheet (Join-Path $previewDirectory 'netpulse-icon-preview.png')
Write-Output ('Icon: {0}; frames: {1}' -f $iconPath, ($sizes -join ', '))
Write-Output ('Preview: {0}' -f (Join-Path $previewDirectory 'netpulse-icon-preview.png'))
