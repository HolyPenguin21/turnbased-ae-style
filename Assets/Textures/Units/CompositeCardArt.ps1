<#
Composites a raw generated illustration onto the reusable card frame (Card_Base.png).
See CARD_ART_PIPELINE.md (Step 2) in this same folder for the full pipeline this belongs to.

See README.md in this same folder for full usage instructions and examples.

Quick reference - all alpha-feather parameters (see README.md for what each one visually does):
    -SideFeatherPercent    left/right fade width, as % of image width      (default 15)
    -TopFeatherPercent     top fade height, as % of image height           (default 15)
    -BottomFadeStartPercent  where the bottom fade begins, % of height     (default 50)
    -BottomFadeEndPercent    where the bottom fade reaches 0, % of height  (default 72)

What this does, in order:
    1. Takes Card_Base.png (832x1216 frame template) and rounds its 4 corners (46px radius
       alpha-cutout) on a fresh in-memory copy - the template file itself is never modified.
    2. Feathers the raw illustration's edges to alpha 0 near the borders, so it blends into the
       frame instead of showing a hard rectangle (widths/heights controlled by the 4 params
       above).
    3. Composites the rounded frame + feathered art onto a new 832x1216 canvas: art is scaled
       (HighQualityBicubic) to fit width into the window left=70 right=760 (690px wide), anchored
       at top=80, height scaled proportionally to preserve aspect ratio.
    4. Saves the result as a PNG.

After running this, still need Step 3 from CARD_ART_PIPELINE.md (fix the Unity .meta import
settings - spriteMode/textureType/alphaIsTransparency/nPOTScale) and Step 4 (wire the guid into
the card catalog). Those aren't done by this script.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ArtPath,

    [Parameter(Mandatory = $true)]
    [string]$OutName,

    [string]$OutDir = "",

    [ValidateRange(0, 50)]
    [double]$SideFeatherPercent = 15,

    [ValidateRange(0, 50)]
    [double]$TopFeatherPercent = 15,

    [ValidateRange(0, 100)]
    [double]$BottomFadeStartPercent = 50,

    [ValidateRange(0, 100)]
    [double]$BottomFadeEndPercent = 72
)

if ($BottomFadeEndPercent -le $BottomFadeStartPercent) {
    throw "BottomFadeEndPercent ($BottomFadeEndPercent) must be greater than BottomFadeStartPercent ($BottomFadeStartPercent)."
}

Add-Type -AssemblyName System.Drawing

# $PSScriptRoot (and $PSCommandPath) have been observed to come back empty at param-block-default
# evaluation time when this script is launched as a nested `powershell -File` child process (e.g.
# from another shell/tool) - that silently turned the old "$PSScriptRoot\IronConcord\GameCards"
# default into "\IronConcord\GameCards", which resolves to the current drive's ROOT, not this
# folder, and wrote output there without any error. By the time the script BODY runs (here),
# $PSScriptRoot is reliably populated, so all path defaults are resolved down here instead of in
# the param block.
$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($ScriptRoot)) {
    if ($MyInvocation.MyCommand.Path) { $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    else { throw "Could not determine the script's own directory - run the .ps1 file directly rather than piping/dot-sourcing it." }
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = "$ScriptRoot\IronConcord\GameCards"
}

$FramePath = "$ScriptRoot\..\General\Card_Base.png"
$CanvasSize = 832, 1216
$CornerRadius = 46
$ArtWindowLeft = 70
$ArtWindowRight = 760
$ArtWindowTop = 80

if (-not (Test-Path $ArtPath)) { throw "Art file not found: $ArtPath" }
if (-not (Test-Path $FramePath)) { throw "Card_Base.png not found at: $FramePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
Write-Host "Output folder: $OutDir"

function New-RoundedFrame {
    param([System.Drawing.Bitmap]$Source, [int]$Radius)

    $bmp = New-Object System.Drawing.Bitmap $Source.Width, $Source.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($Source, 0, 0)
    $g.Dispose()

    # Alpha-cutout the 4 corners: any pixel outside the rounded-rect radius from each corner
    # center gets alpha 0. Cheap per-pixel distance check, only near the corners.
    $w = $bmp.Width; $h = $bmp.Height
    $corners = @(
        @{ CX = $Radius; CY = $Radius },                    # top-left
        @{ CX = $w - $Radius; CY = $Radius },                # top-right
        @{ CX = $Radius; CY = $h - $Radius },                # bottom-left
        @{ CX = $w - $Radius; CY = $h - $Radius }            # bottom-right
    )
    foreach ($c in $corners) {
        $x0 = [Math]::Max(0, $c.CX - $Radius)
        $x1 = [Math]::Min($w - 1, $c.CX + $Radius)
        $y0 = [Math]::Max(0, $c.CY - $Radius)
        $y1 = [Math]::Min($h - 1, $c.CY + $Radius)
        for ($y = $y0; $y -le $y1; $y++) {
            for ($x = $x0; $x -le $x1; $x++) {
                # Only actually a "corner" quadrant pixel if it's on the outside side of the
                # corner center in both axes (otherwise it's the straight edge, leave it alone).
                $isLeft = $x -lt $c.CX
                $isTop = $y -lt $c.CY
                $cornerIsLeft = $c.CX -eq $Radius
                $cornerIsTop = $c.CY -eq $Radius
                if (($isLeft -eq $cornerIsLeft) -and ($isTop -eq $cornerIsTop)) {
                    $dx = $x - $c.CX
                    $dy = $y - $c.CY
                    if (($dx * $dx + $dy * $dy) -gt ($Radius * $Radius)) {
                        $bmp.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                    }
                }
            }
        }
    }
    return $bmp
}

function New-FeatheredArt {
    param(
        [System.Drawing.Bitmap]$Source,
        [double]$SidePercent,
        [double]$TopPercent,
        [double]$BottomStartPercent,
        [double]$BottomEndPercent
    )

    $w = $Source.Width; $h = $Source.Height
    $edgeH = [Math]::Max(1, [int]($w * ($SidePercent / 100.0)))
    $edgeTop = [Math]::Max(1, [int]($h * ($TopPercent / 100.0)))
    $bottomStart = [int]($h * ($BottomStartPercent / 100.0))
    $bottomEnd = [int]($h * ($BottomEndPercent / 100.0))

    # Per-pixel work is done on raw BGRA byte buffers (LockBits + Marshal.Copy) instead of
    # GetPixel/SetPixel - the latter are GDI+ calls with heavy per-call overhead and turn a
    # ~1-megapixel image into a multi-minute operation with zero visible progress in between.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $srcData = $Source.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $srcData.Stride
    $byteCount = $stride * $h
    $buffer = New-Object byte[] $byteCount
    [System.Runtime.InteropServices.Marshal]::Copy($srcData.Scan0, $buffer, 0, $byteCount)
    $Source.UnlockBits($srcData)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastReportedPercent = -1

    for ($y = 0; $y -lt $h; $y++) {
        if ($y -lt $edgeTop) {
            $vFactor = $y / [double]$edgeTop
        } elseif ($y -ge $bottomStart) {
            if ($y -ge $bottomEnd) {
                $vFactor = 0.0
            } else {
                $vFactor = 1.0 - (($y - $bottomStart) / [double]($bottomEnd - $bottomStart))
            }
        } else {
            $vFactor = 1.0
        }

        $rowOffset = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            if ($x -lt $edgeH) {
                $hFactor = $x / [double]$edgeH
            } elseif ($x -ge ($w - $edgeH)) {
                $hFactor = ($w - 1 - $x) / [double]$edgeH
            } else {
                $hFactor = 1.0
            }

            $factor = [Math]::Min($hFactor, $vFactor)
            if ($factor -lt 0.999) {
                # Format32bppArgb byte order in memory is B, G, R, A - only alpha changes.
                $alphaIdx = $rowOffset + ($x * 4) + 3
                $clamped = [Math]::Max(0.0, [Math]::Min(1.0, $factor))
                $buffer[$alphaIdx] = [byte]([Math]::Round($buffer[$alphaIdx] * $clamped))
            }
        }

        $percent = [int](100 * ($y + 1) / $h)
        if ($percent -ne $lastReportedPercent -and ($percent % 10 -eq 0)) {
            Write-Host "  Feathering: $percent% (row $($y + 1)/$h, $([int]$sw.Elapsed.TotalSeconds)s elapsed)"
            $lastReportedPercent = $percent
        }
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $dstData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    [System.Runtime.InteropServices.Marshal]::Copy($buffer, 0, $dstData.Scan0, $byteCount)
    $bmp.UnlockBits($dstData)

    return $bmp
}

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "Loading frame and art..."
$frameSrc = [System.Drawing.Bitmap]::FromFile($FramePath)
$artSrc = [System.Drawing.Bitmap]::FromFile($ArtPath)
Write-Host "  Frame: $($frameSrc.Width)x$($frameSrc.Height)   Art: $($artSrc.Width)x$($artSrc.Height)"

Write-Host "Rounding frame corners..."
$roundedFrame = New-RoundedFrame -Source $frameSrc -Radius $CornerRadius

Write-Host "Feathering art edges (sides=$SideFeatherPercent% top=$TopFeatherPercent% bottom=$BottomFadeStartPercent%-$BottomFadeEndPercent%)..."
$featheredArt = New-FeatheredArt -Source $artSrc -SidePercent $SideFeatherPercent -TopPercent $TopFeatherPercent -BottomStartPercent $BottomFadeStartPercent -BottomEndPercent $BottomFadeEndPercent

Write-Host "Compositing... ($([int]$totalSw.Elapsed.TotalSeconds)s elapsed so far)"
$canvas = New-Object System.Drawing.Bitmap $CanvasSize[0], $CanvasSize[1], ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$g.DrawImage($roundedFrame, 0, 0, $CanvasSize[0], $CanvasSize[1])

$artWindowWidth = $ArtWindowRight - $ArtWindowLeft
$artScaledHeight = [int]($featheredArt.Height * ($artWindowWidth / [double]$featheredArt.Width))
$g.DrawImage($featheredArt, $ArtWindowLeft, $ArtWindowTop, $artWindowWidth, $artScaledHeight)

$g.Dispose()

$outPath = Join-Path $OutDir "$OutName.png"
$canvas.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)

$frameSrc.Dispose(); $artSrc.Dispose(); $roundedFrame.Dispose(); $featheredArt.Dispose(); $canvas.Dispose()

Write-Host "Saved: $outPath  (total $([int]$totalSw.Elapsed.TotalSeconds)s)"
Write-Host "Next: fix Unity import settings (Step 3) and wire the guid into the card catalog (Step 4) - see CARD_ART_PIPELINE.md."
