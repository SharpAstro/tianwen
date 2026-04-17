# Full Milky Way texture pipeline for TianWen's sky map.
#
# Stitches together the three .NET file-based tools:
#   1. reproject_planck_dust.cs  -- downloads the Planck GNILC dust OPACITY
#      HEALPix FITS (~400 MB) and reprojects to tools/data/dust_2048.f32.
#   2. reproject_planck_dust.cs  -- downloads the Planck GNILC dust RADIANCE
#      HEALPix FITS (~200 MB) and reprojects to tools/data/radiance_2048.f32.
#      The radiance map is the continuous luminance channel (no star-point
#      noise).
#   3. generate_milkyway.cs      -- composites Tycho-2 colour + Planck
#      radiance brightness + exp(-k * tau) dust extinction into the final
#      src/TianWen.UI.Gui/Resources/milkyway.bgra.lz texture.
#
# Downloads are cached under tools/data/ (gitignored). Re-running skips
# any already-downloaded FITS. Full pipeline from cold cache is ~5-6 min
# (mostly the 400 MB opacity download); re-bakes with warm cache run in
# ~5 s (Tycho-2 load + reproject + lzip).
#
# Usage:
#   pwsh tools/bake_milkyway.ps1
#   pwsh tools/bake_milkyway.ps1 -Width 4096 -K 2.5
#
# Options pass through to generate_milkyway.cs; see that file's header for
# the full list.

[CmdletBinding()]
param(
    [int]$Width = 2048,
    [float]$K = 10.0,
    # Light blur on the luminance channel. Radiance is already continuous, so
    # sigma ~1 preserves the fine dust-lane detail that strong extinction
    # punches through. Only relevant when Tycho-2-only mode is used via
    # -SkipRadiance (then bump to ~6 to hide salt-and-pepper).
    [float]$BlurSigma = 1.0,
    [float]$MinMag = 8.5,
    [float]$Saturation = 2.0,
    [float]$Brightness = 0.5,
    [float]$ColorBlur = 6.0,
    [float]$Warmth = 0.25,
    [float]$DustReddening = 0.3,
    # 1.0 = pure radiance, 0.0 = pure Tycho-2 density. 0.85 is a subtle
    # Tycho-2 nudge that brings out where stellar bulk concentrates without
    # introducing the salt-and-pepper.
    [float]$LuminanceMix = 0.85,
    [switch]$SkipRadiance,
    [switch]$SkipExtinction
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $Height = $Width / 2
    $dustFile     = "tools/data/dust_${Width}.f32"
    $radianceFile = "tools/data/radiance_${Width}.f32"

    $opacityUrl  = "https://irsa.ipac.caltech.edu/data/Planck/release_2/all-sky-maps/maps/component-maps/foregrounds/COM_CompMap_Dust-GNILC-Model-Opacity_2048_R2.01.fits"
    $radianceUrl = "https://irsa.ipac.caltech.edu/data/Planck/release_2/all-sky-maps/maps/component-maps/foregrounds/COM_CompMap_Dust-GNILC-Radiance_2048_R2.00.fits"

    if (-not $SkipExtinction) {
        Write-Host "`n=== Step 1: Reproject Planck dust OPACITY ($Width x $Height) ===" -ForegroundColor Cyan
        dotnet run tools/reproject_planck_dust.cs -- --url $opacityUrl --output $dustFile --width $Width --height $Height
        if ($LASTEXITCODE -ne 0) { throw "dust opacity reprojection failed" }
    } else {
        Write-Host "Skipping dust opacity step (-SkipExtinction)" -ForegroundColor Yellow
    }

    if (-not $SkipRadiance) {
        Write-Host "`n=== Step 2: Reproject Planck dust RADIANCE ($Width x $Height) ===" -ForegroundColor Cyan
        dotnet run tools/reproject_planck_dust.cs -- --url $radianceUrl --output $radianceFile --width $Width --height $Height
        if ($LASTEXITCODE -ne 0) { throw "radiance reprojection failed" }
    } else {
        Write-Host "Skipping radiance step (-SkipRadiance); Tycho-2 will be the luminance source" -ForegroundColor Yellow
    }

    Write-Host "`n=== Step 3: Bake Milky Way texture ===" -ForegroundColor Cyan
    $bakeArgs = @(
        'tools/generate_milkyway.cs', '--',
        '--width', $Width, '--height', $Height,
        '--k', $K, '--blur-sigma', $BlurSigma,
        '--min-mag', $MinMag, '--saturation', $Saturation,
        '--brightness', $Brightness, '--color-blur', $ColorBlur,
        '--warmth', $Warmth, '--dust-reddening', $DustReddening,
        '--luminance-mix', $LuminanceMix
    )
    if (-not $SkipRadiance)   { $bakeArgs += @('--luminance',    $radianceFile) }
    if (-not $SkipExtinction) { $bakeArgs += @('--dust-opacity', $dustFile) }
    dotnet run @bakeArgs
    if ($LASTEXITCODE -ne 0) { throw "bake failed" }

    $outputPath = "src/TianWen.UI.Gui/Resources/milkyway.bgra.lz"
    $outputSize = (Get-Item $outputPath).Length
    Write-Host "`nDone. $outputPath = $([math]::Round($outputSize/1MB, 2)) MB" -ForegroundColor Green
}
finally {
    Pop-Location
}
