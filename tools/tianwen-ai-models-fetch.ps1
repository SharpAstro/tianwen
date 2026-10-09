# Materialize TianWen's AI models (GraXpert BGE, and TianWen's own in-repo models)
# into %LOCALAPPDATA%\TianWen\models.
#
# A DEVELOPER tool, not an install step. Nothing the product prints points a user
# here, by design.
#
# SETI Astro's AI4 models are no longer fetched, and nothing in TianWen loads them.
# Their licence (COSMIC_CLARITY_LICENSE.txt, published 2026-09-24 on the release
# that hosts them) allows use only within SASpro unless the author consents in
# writing, and TianWen dropped that tier on 2026-09-26. The phases that downloaded
# SASPro_Models_AI4*.zip and hardlinked SASpro's own models folder went with it.
#
# Sourcing strategy:
#   1. GraXpert's background-extraction model: detect GraXpert's own cache and
#      hardlink the newest version into TianWen's tree (zero disk cost on NTFS;
#      plain copy when the hardlink fails, e.g. across volumes). Never downloaded:
#      GraXpert's models are CC-BY-NC-SA-4.0 and TianWen redistributes no
#      third-party weights, so an absent GraXpert prints a hint and is skipped.
#      The product also finds it in GraXpert's cache on its own (ModelResolver),
#      so this copy is a convenience, not a requirement.
#   2. TianWen's own models (src/TianWen.AI.Imaging/models/, Git LFS) hardlink
#      from this checkout; a pointer-stub checkout (no git-lfs installed) falls
#      back to downloading the LFS object bytes from GitHub's media host.
#   3. Idempotent: files already present under TianWen\models are skipped, so
#      re-runs are safe and cheap.
#
# Usage:
#   pwsh tools/tianwen-ai-models-fetch.ps1
#   pwsh tools/tianwen-ai-models-fetch.ps1 -OutputDir D:\my\models
#   pwsh tools/tianwen-ai-models-fetch.ps1 -NoGraXpert        # TianWen's own models only
#   pwsh tools/tianwen-ai-models-fetch.ps1 -NoDownload        # checkout only; report a pointer stub
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$GraXpertDir,
    [switch]$NoGraXpert,
    [switch]$NoDownload
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Suppress Invoke-WebRequest's default progress bar; it stalls pwsh on large files.
$ProgressPreference = 'SilentlyContinue'

function Get-DefaultOutputDir {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        $base = $env:LOCALAPPDATA
        if (-not $base) { $base = Join-Path $HOME 'AppData/Local' }
        return Join-Path $base 'TianWen/models'
    }
    if ($IsMacOS) {
        return Join-Path $HOME 'Library/Application Support/TianWen/models'
    }
    return Join-Path $HOME '.local/share/TianWen/models'
}

function Get-DefaultGraXpertDir {
    # GraXpert (Steffenhir/GraXpert; GPL-3.0 code, CC-BY-NC-SA-4.0 models) stores its ONNX models under
    # %LOCALAPPDATA%/GraXpert/GraXpert/<bucket>/<version>/model.onnx -- one
    # subdir per model kind (bge-ai-models, denoise-ai-models, etc.) with
    # one semver-named version dir each. Only the BGE bucket is consumed
    # (background extraction); TianWen denoises with its own model.
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        $base = $env:LOCALAPPDATA
        if (-not $base) { $base = Join-Path $HOME 'AppData/Local' }
        return Join-Path $base 'GraXpert/GraXpert'
    }
    if ($IsMacOS) {
        return Join-Path $HOME 'Library/Application Support/GraXpert/GraXpert'
    }
    return Join-Path $HOME '.local/share/GraXpert/GraXpert'
}

if (-not $OutputDir)    { $OutputDir    = Get-DefaultOutputDir }
if (-not $GraXpertDir)  { $GraXpertDir  = Get-DefaultGraXpertDir }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Write-Host ("Output: {0}" -f $OutputDir) -ForegroundColor DarkGray

function Try-Hardlink {
    # Returns $true on success, $false if hardlink isn't supported here
    # (cross-volume, non-NTFS, insufficient privilege, ...). Caller falls back
    # to Copy-Item so the user still ends up with the file.
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Target)
    try {
        New-Item -ItemType HardLink -Path $Target -Value $Source -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

# Phase 1: GraXpert background-extraction (BGE) model. Single file, no zip,
# detect-and-hardlink only (see the header for why there is no download).
function Materialize-GraXpertBge {
    param([Parameter(Mandatory)][string]$GraXpertRoot, [Parameter(Mandatory)][string]$Output)
    $bgeRoot = Join-Path $GraXpertRoot 'bge-ai-models'
    if (-not (Test-Path -LiteralPath $bgeRoot)) {
        Write-Host ("  GraXpert BGE dir not found: {0}" -f $bgeRoot) -ForegroundColor DarkGray
        Write-Host "  -> install GraXpert (https://github.com/Steffenhir/GraXpert) and run it at least once to populate the model cache, then re-run this script." -ForegroundColor DarkGray
        return $false
    }

    # Pick the highest-semver-named subdir that contains a model.onnx.
    $candidates = Get-ChildItem -LiteralPath $bgeRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'model.onnx')) } |
        Sort-Object -Property @{Expression = { [version]$_.Name }} -Descending
    if (-not $candidates) {
        Write-Host ("  no versioned BGE model under {0}" -f $bgeRoot) -ForegroundColor Yellow
        return $false
    }
    $picked = $candidates[0]
    $source = Join-Path $picked.FullName 'model.onnx'
    # Flatten to a versioned name in the TianWen models tree, so a future BGE
    # version can live alongside the old one for an A/B comparison.
    $target = Join-Path $Output ("graxpert_bge_v{0}.onnx" -f $picked.Name)
    # Also keep an unversioned alias so downstream code (IModelResolver) can
    # request "graxpert_bge.onnx" without baking the version in. Re-link the
    # alias to the newest version on every run.
    $alias  = Join-Path $Output 'graxpert_bge.onnx'

    $size = (Get-Item -LiteralPath $source).Length
    Write-Host ("  GraXpert BGE: v{0} ({1:N0} MB)" -f $picked.Name, ($size / 1MB)) -ForegroundColor Cyan

    if (Test-Path -LiteralPath $target) {
        Write-Host ("    skipped:    {0,5} (already present)" -f 1) -ForegroundColor DarkGray
    } elseif (Try-Hardlink -Source $source -Target $target) {
        Write-Host ("    hardlinked: {0,5} -> {1}" -f 1, $target) -ForegroundColor Green
    } else {
        Copy-Item -LiteralPath $source -Destination $target -Force
        Write-Host ("    copied:     {0,5} (hardlink failed) -> {1}" -f 1, $target) -ForegroundColor Yellow
    }

    if (Test-Path -LiteralPath $alias) { Remove-Item -LiteralPath $alias -Force }
    if (Try-Hardlink -Source $target -Target $alias) {
        Write-Host ("    aliased:    {0,5} -> graxpert_bge.onnx" -f 1) -ForegroundColor Green
    } else {
        Copy-Item -LiteralPath $target -Destination $alias -Force
        Write-Host ("    aliased:    {0,5} -> graxpert_bge.onnx (copy)" -f 1) -ForegroundColor Yellow
    }
    return $true
}

if (-not $NoGraXpert) {
    Write-Host "[1/2] Materializing GraXpert BGE model" -ForegroundColor Cyan
    Materialize-GraXpertBge -GraXpertRoot $GraXpertDir -Output $OutputDir | Out-Null
} else {
    Write-Host "[1/2] GraXpert skipped (-NoGraXpert)" -ForegroundColor DarkGray
}

# Phase 2: TianWen's own models, shipped IN this repo
# (src/TianWen.AI.Imaging/models/). Preferred source is the checkout beside this
# script, hardlink-else-copy. These weights are LFS objects again since
# 2026-09-06 (between 2026-08-19 and then a .gitattributes exemption made them a
# plain git blob, so a checkout held them outright and the pointer-stub path
# below was dead code for them). A clone made without git-lfs holds a ~130-byte
# pointer stub instead of weights, in which case the LFS object bytes are fetched
# from GitHub's media host (which serves the real content for a public repo; the
# plain raw host would serve the stub again).
# Each model travels with its <stem>.contract.json (a plain tracked file, not an LFS object): the loader
# refuses a model whose contract is not beside it in the same directory (#824).
$tianwenNativeModels = @(
    'tianwen_denoise_osc_convmapb_s2.onnx', 'tianwen_denoise_osc_convmapb_s2.contract.json',
    'tianwen_deconv_operator_e34d_s0.onnx', 'tianwen_deconv_operator_e34d_s0.contract.json')
$tianwenRepoModelsDir = Join-Path $PSScriptRoot '..' 'src' 'TianWen.AI.Imaging' 'models'
$tianwenLfsMediaBase = 'https://media.githubusercontent.com/media/SharpAstro/tianwen/main/src/TianWen.AI.Imaging/models'

function Test-LfsPointerStub {
    param([Parameter(Mandatory)][string]$Path)
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -gt 1024) { return $false }
    $head = Get-Content -LiteralPath $Path -TotalCount 1 -ErrorAction SilentlyContinue
    return $head -is [string] -and $head.StartsWith('version https://git-lfs')
}

function Materialize-TianWenModel {
    param([Parameter(Mandatory)][string]$Name)
    $target = Join-Path $OutputDir $Name

    if (Test-Path -LiteralPath $target) {
        Write-Host ("  skipped:    {0} (already present)" -f $Name) -ForegroundColor DarkGray
        return
    }

    $source = Join-Path $tianwenRepoModelsDir $Name
    if ((Test-Path -LiteralPath $source -PathType Leaf) -and -not (Test-LfsPointerStub -Path $source)) {
        if (Try-Hardlink -Source $source -Target $target) {
            Write-Host ("  hardlinked: {0} (from repo checkout)" -f $Name) -ForegroundColor Green
        } else {
            Copy-Item -LiteralPath $source -Destination $target -Force
            Write-Host ("  copied:     {0} (from repo checkout; hardlink failed)" -f $Name) -ForegroundColor Yellow
        }
        return
    }

    if ($NoDownload) {
        Write-Host ("  MISSING:    {0} -- checkout has no weights (LFS pointer stub?) and -NoDownload is set. Run 'git lfs pull --include=*.onnx' and re-run." -f $Name) -ForegroundColor Yellow
        return
    }

    $url = "$tianwenLfsMediaBase/$Name"
    Write-Host ("  downloading {0} from {1} ..." -f $Name, $url) -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $target -ErrorAction Stop
    if (Test-LfsPointerStub -Path $target) {
        Remove-Item -LiteralPath $target -Force
        throw ("Downloaded {0} is itself an LFS pointer stub -- the media host did not resolve it. Run 'git lfs pull --include=*.onnx' in the repo instead." -f $Name)
    }
    Write-Host ("  done:       {0} ({1:N1} MB)" -f $Name, ((Get-Item -LiteralPath $target).Length / 1MB)) -ForegroundColor Green
}

Write-Host "[2/2] Materializing TianWen native models" -ForegroundColor Cyan
foreach ($name in $tianwenNativeModels) { Materialize-TianWenModel -Name $name }

Write-Host ""
Write-Host "Done. Models in: $OutputDir" -ForegroundColor Green
