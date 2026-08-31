<#
Composites raw generated illustrations onto the reusable card frame (Card_Base.png).
See CARD_ART_PIPELINE.md (Step 2) in this same folder for the full pipeline this belongs to.

See README.md in this same folder for full usage instructions and examples.

Batch mode: processes every *.png found directly inside -InputFolder (not recursive into
subfolders) and writes TWO composited outputs per source image:
    Output 1 (-OutDir1) - bottom faded out for description text. Same file name as the source.
    Output 2 (-OutDir2) - full art, no bottom wipe (plain symmetric top/bottom fade instead).
                          File name = source name + "_Full".

Quick reference - all alpha-feather parameters (see README.md for what each one visually does):
    -SideFeatherPercent        left/right fade width, % of image width        (default 15) - shared by both outputs
    -TopFeatherPercent         top fade height, % of image height             (default 15) - output 1 only
    -BottomFadeStartPercent    where output 1's bottom fade begins, % of height (default 50)
    -BottomFadeEndPercent      where output 1's bottom fade reaches 0, % of height (default 72)
    -TopBottomFeatherPercent   top AND bottom fade height, % of height        (default 15) - output 2 only

What this does, per source image, in order:
    1. Takes Card_Base.png (832x1216 frame template) and rounds its 4 corners (46px radius
       anti-aliased alpha-cutout) on a fresh in-memory copy - the template file itself is never modified. This
       is done once and reused across the whole batch, not per image.
    2. Feathers the raw illustration's edges to alpha 0 near the borders with rounded fade intersections and SmoothStep easing, twice per image - once
       with the text-wipe bottom (output 1) and once with a plain symmetric top/bottom edge fade
       (output 2) - so it blends into the frame instead of showing a hard rectangle.
    3. Composites the rounded frame + feathered art onto a new 832x1216 canvas: art is scaled
       (HighQualityBicubic) to fit width into the window left=70 right=760 (690px wide), anchored
       at top=80, height scaled proportionally to preserve aspect ratio.
    4. Saves both results as PNGs into -OutDir1 / -OutDir2.

After running this, still need Step 3 from CARD_ART_PIPELINE.md (fix the Unity .meta import
settings - spriteMode/textureType/alphaIsTransparency/nPOTScale) and Step 4 (wire the guid into
the card catalog). Those aren't done by this script.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$InputFolder,

    [string]$OutDir1 = "",
    [string]$OutDir2 = "",

    [ValidateRange(0, 50)]
    [double]$SideFeatherPercent = 15,

    [ValidateRange(0, 50)]
    [double]$TopFeatherPercent = 15,

    [ValidateRange(0, 100)]
    [double]$BottomFadeStartPercent = 50,

    [ValidateRange(0, 100)]
    [double]$BottomFadeEndPercent = 72,

    [ValidateRange(0, 50)]
    [double]$TopBottomFeatherPercent = 15
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

if (-not (Test-Path $InputFolder)) { throw "Input folder not found: $InputFolder" }
$InputFolder = [System.IO.Path]::GetFullPath($InputFolder)

if ([string]::IsNullOrWhiteSpace($OutDir1)) { $OutDir1 = Join-Path $InputFolder "GameCards" }
if ([string]::IsNullOrWhiteSpace($OutDir2)) { $OutDir2 = Join-Path $InputFolder "GameCards_Full" }

$FramePath = "$ScriptRoot\..\General\Card_Base.png"
$CanvasSize = 832, 1216
$CornerRadius = 46
$ArtWindowLeft = 70
$ArtWindowRight = 760
$ArtWindowTop = 80
$Output2Suffix = "_Full"

if (-not (Test-Path $FramePath)) { throw "Card_Base.png not found at: $FramePath" }
if (-not (Test-Path $OutDir1)) { New-Item -ItemType Directory -Path $OutDir1 -Force | Out-Null }
if (-not (Test-Path $OutDir2)) { New-Item -ItemType Directory -Path $OutDir2 -Force | Out-Null }
$OutDir1 = [System.IO.Path]::GetFullPath($OutDir1)
$OutDir2 = [System.IO.Path]::GetFullPath($OutDir2)
Write-Host "Input folder: $InputFolder"
Write-Host "Output folder 1 (text-wipe bottom): $OutDir1"
Write-Host "Output folder 2 (full art, no wipe): $OutDir2"

# Non-recursive on purpose: OutDir1/OutDir2 default to subfolders of InputFolder, so a plain
# top-level listing here never picks up files this same run just wrote.
$artFiles = Get-ChildItem -Path $InputFolder -File -Filter *.png | Sort-Object Name
if ($artFiles.Count -eq 0) { throw "No .png files found directly in: $InputFolder" }
Write-Host "Found $($artFiles.Count) source image(s)."

function New-RoundedFrame {
    param([System.Drawing.Bitmap]$Source, [int]$Radius)

    $bmp = New-Object System.Drawing.Bitmap $Source.Width, $Source.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($Source, 0, 0)
    $g.Dispose()

    # Anti-aliased alpha cutout for the four outer frame corners.
    # The old code used a binary 0/255 alpha cut, which can leave a visibly jagged edge.
    # Here the last ~1 px around the circle gets partial alpha coverage.
    $w = $bmp.Width; $h = $bmp.Height
    $corners = @(
        @{ CX = $Radius; CY = $Radius },
        @{ CX = $w - $Radius; CY = $Radius },
        @{ CX = $Radius; CY = $h - $Radius },
        @{ CX = $w - $Radius; CY = $h - $Radius }
    )

    foreach ($c in $corners) {
        $x0 = [Math]::Max(0, $c.CX - $Radius - 1)
        $x1 = [Math]::Min($w - 1, $c.CX + $Radius + 1)
        $y0 = [Math]::Max(0, $c.CY - $Radius - 1)
        $y1 = [Math]::Min($h - 1, $c.CY + $Radius + 1)

        for ($y = $y0; $y -le $y1; $y++) {
            for ($x = $x0; $x -le $x1; $x++) {
                $isLeft = $x -lt $c.CX
                $isTop = $y -lt $c.CY
                $cornerIsLeft = $c.CX -eq $Radius
                $cornerIsTop = $c.CY -eq $Radius

                if (($isLeft -eq $cornerIsLeft) -and ($isTop -eq $cornerIsTop)) {
                    $dx = $x - $c.CX
                    $dy = $y - $c.CY
                    $distance = [Math]::Sqrt(($dx * $dx) + ($dy * $dy))

                    # 1.0 well inside the radius, 0.0 well outside, fractional around the edge.
                    $coverage = ($Radius + 0.5) - $distance
                    $coverage = [Math]::Max(0.0, [Math]::Min(1.0, $coverage))

                    if ($coverage -lt 0.999) {
                        if ($coverage -le 0.001) {
                            $bmp.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                        } else {
                            $p = $bmp.GetPixel($x, $y)
                            $newAlpha = [byte]([Math]::Round($p.A * $coverage))
                            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($newAlpha, $p.R, $p.G, $p.B))
                        }
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
        # 'Wipe'  - bottom ramps from full alpha at BottomStartPercent down to 0 at BottomEndPercent
        #           (leaves a large blank area for card text - output 1).
        # 'Edge'  - bottom mirrors the top's own formula: the outer BottomEdgePercent of the image
        #           fades from 0 at the border to full alpha inward (output 2, no text wipe).
        [ValidateSet('Wipe', 'Edge')]
        [string]$BottomMode,
        [double]$BottomStartPercent = 0,
        [double]$BottomEndPercent = 0,
        [double]$BottomEdgePercent = 0
    )

    $w = $Source.Width; $h = $Source.Height
    $edgeH = [Math]::Max(1, [int]($w * ($SidePercent / 100.0)))
    $edgeTop = [Math]::Max(1, [int]($h * ($TopPercent / 100.0)))
    if ($BottomMode -eq 'Wipe') {
        $bottomStart = [int]($h * ($BottomStartPercent / 100.0))
        $bottomEnd = [int]($h * ($BottomEndPercent / 100.0))
    } else {
        $edgeBottom = [Math]::Max(1, [int]($h * ($BottomEdgePercent / 100.0)))
    }

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
        } elseif ($BottomMode -eq 'Wipe' -and $y -ge $bottomStart) {
            if ($y -ge $bottomEnd) {
                $vFactor = 0.0
            } else {
                $vFactor = 1.0 - (($y - $bottomStart) / [double]($bottomEnd - $bottomStart))
            }
        } elseif ($BottomMode -eq 'Edge' -and $y -ge ($h - $edgeBottom)) {
            $vFactor = ($h - 1 - $y) / [double]$edgeBottom
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

            # Clamp the independent horizontal / vertical fades first.
            $hFactor = [Math]::Max(0.0, [Math]::Min(1.0, $hFactor))
            $vFactor = [Math]::Max(0.0, [Math]::Min(1.0, $vFactor))

            # Rounded corner blend.
            # Min(h,v) creates square/L-shaped intersections where side and top/bottom fades meet.
            # Treat the distance from the fully opaque interior (1,1) radially instead, which makes
            # those intersections curve smoothly around the corner.
            $fadeX = 1.0 - $hFactor
            $fadeY = 1.0 - $vFactor
            $factor = 1.0 - [Math]::Sqrt(($fadeX * $fadeX) + ($fadeY * $fadeY))
            $factor = [Math]::Max(0.0, [Math]::Min(1.0, $factor))

            # SmoothStep easing removes the visibly linear alpha ramp while preserving 0 and 1.
            $factor = $factor * $factor * (3.0 - (2.0 * $factor))

            if ($factor -lt 0.999) {
                # Format32bppArgb byte order in memory is B, G, R, A - only alpha changes.
                $alphaIdx = $rowOffset + ($x * 4) + 3
                $buffer[$alphaIdx] = [byte]([Math]::Round($buffer[$alphaIdx] * $factor))
            }
        }

        $percent = [int](100 * ($y + 1) / $h)
        if ($percent -ne $lastReportedPercent -and ($percent % 20 -eq 0)) {
            Write-Host "    Feathering: $percent% (row $($y + 1)/$h, $([int]$sw.Elapsed.TotalSeconds)s elapsed)"
            $lastReportedPercent = $percent
        }
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $dstData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    [System.Runtime.InteropServices.Marshal]::Copy($buffer, 0, $dstData.Scan0, $byteCount)
    $bmp.UnlockBits($dstData)

    return $bmp
}

function New-CompositeCard {
    param(
        [System.Drawing.Bitmap]$RoundedFrame,
        [System.Drawing.Bitmap]$FeatheredArt,
        [string]$OutPath
    )

    $canvas = New-Object System.Drawing.Bitmap $CanvasSize[0], $CanvasSize[1], ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $g.DrawImage($RoundedFrame, 0, 0, $CanvasSize[0], $CanvasSize[1])

    $artWindowWidth = $ArtWindowRight - $ArtWindowLeft
    $artScaledHeight = [int]($FeatheredArt.Height * ($artWindowWidth / [double]$FeatheredArt.Width))
    $g.DrawImage($FeatheredArt, $ArtWindowLeft, $ArtWindowTop, $artWindowWidth, $artScaledHeight)

    $g.Dispose()
    $canvas.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "Loading frame..."
$frameSrc = [System.Drawing.Bitmap]::FromFile($FramePath)
Write-Host "  Frame: $($frameSrc.Width)x$($frameSrc.Height)"

Write-Host "Rounding frame corners (once, reused for the whole batch)..."
$roundedFrame = New-RoundedFrame -Source $frameSrc -Radius $CornerRadius
$frameSrc.Dispose()

$fileIndex = 0
foreach ($file in $artFiles) {
    $fileIndex++
    Write-Host ""
    Write-Host "[$fileIndex/$($artFiles.Count)] $($file.Name)  ($([int]$totalSw.Elapsed.TotalSeconds)s elapsed so far)"

    $artSrc = [System.Drawing.Bitmap]::FromFile($file.FullName)
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

    Write-Host "  Output 1 - feathering (sides=$SideFeatherPercent% top=$TopFeatherPercent% bottom=$BottomFadeStartPercent%-$BottomFadeEndPercent%)..."
    $feathered1 = New-FeatheredArt -Source $artSrc -SidePercent $SideFeatherPercent -TopPercent $TopFeatherPercent -BottomMode Wipe -BottomStartPercent $BottomFadeStartPercent -BottomEndPercent $BottomFadeEndPercent
    $outPath1 = Join-Path $OutDir1 "$baseName.png"
    New-CompositeCard -RoundedFrame $roundedFrame -FeatheredArt $feathered1 -OutPath $outPath1
    $feathered1.Dispose()
    Write-Host "  Output 1 saved: $outPath1"

    Write-Host "  Output 2 - feathering (sides=$SideFeatherPercent% top/bottom=$TopBottomFeatherPercent%)..."
    $feathered2 = New-FeatheredArt -Source $artSrc -SidePercent $SideFeatherPercent -TopPercent $TopBottomFeatherPercent -BottomMode Edge -BottomEdgePercent $TopBottomFeatherPercent
    $outPath2 = Join-Path $OutDir2 "$baseName$Output2Suffix.png"
    New-CompositeCard -RoundedFrame $roundedFrame -FeatheredArt $feathered2 -OutPath $outPath2
    $feathered2.Dispose()
    Write-Host "  Output 2 saved: $outPath2"

    $artSrc.Dispose()
}

$roundedFrame.Dispose()

Write-Host ""
Write-Host "Done: $($artFiles.Count) source image(s), $($artFiles.Count * 2) file(s) written (total $([int]$totalSw.Elapsed.TotalSeconds)s)"
Write-Host "Next: fix Unity import settings (Step 3) and wire the guid into the card catalog (Step 4) - see CARD_ART_PIPELINE.md."
