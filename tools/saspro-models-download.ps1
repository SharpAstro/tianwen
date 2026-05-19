# Download + install SetiAstroSuite Pro's AI4 + Walking Noise model bundles
# (~1.8 GB) from the GitHub release. Standalone -- no Suite Pro install needed.
#
# What this does:
#   1. Downloads SASPro_Models_AI4.zip and SASPro_Models_Walking.zip from
#      github.com/setiastro/setiastrosuitepro releases (tag benchmarkFIT).
#   2. Caches the .zip files so re-runs skip the download.
#   3. Extracts overlay-style into the install dir (strips a single top-level
#      folder if present, mirroring install_models_zip() in SetiAstroSuite Pro's
#      model_manager.py).
#
# Default install dir matches what Suite Pro reads at runtime:
#   Windows:  $env:LOCALAPPDATA\SASpro\models
#   Linux:    ~/.local/share/SASpro/models  (best-effort)
#   macOS:    ~/Library/Application Support/SASpro/models  (best-effort)
#
# When to run this:
#   - You want the .pth / .onnx weights locally without launching Suite Pro
#     (e.g. CI prep, inspection, scripted setup).
#   - You're iterating on the Cosmic Clarity engine code and need a known-good
#     model set on disk.
#
# Usage:
#   pwsh tools/saspro-models-download.ps1
#   pwsh tools/saspro-models-download.ps1 -NoWalking
#   pwsh tools/saspro-models-download.ps1 -OutputDir D:\my\models
#   pwsh tools/saspro-models-download.ps1 -Force        # redownload cached zips
#   pwsh tools/saspro-models-download.ps1 -NoExtract    # download only
#
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$CacheDir,
    [switch]$NoWalking,
    [switch]$Force,
    [switch]$NoExtract
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Suppress Invoke-WebRequest's default progress bar; it stalls pwsh on multi-GB files.
$ProgressPreference = 'SilentlyContinue'

$githubBase = 'https://github.com/setiastro/setiastrosuitepro/releases/download/benchmarkFIT'

function Get-DefaultInstallDir {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        $base = $env:LOCALAPPDATA
        if (-not $base) { $base = Join-Path $HOME 'AppData/Local' }
        return Join-Path $base 'SASpro/models'
    }
    if ($IsMacOS) {
        return Join-Path $HOME 'Library/Application Support/SASpro/models'
    }
    return Join-Path $HOME '.local/share/SASpro/models'
}

if (-not $OutputDir) { $OutputDir = Get-DefaultInstallDir }
if (-not $CacheDir)  { $CacheDir  = Join-Path $HOME '.cache/saspro-models' }

New-Item -ItemType Directory -Path $CacheDir   -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir  -Force | Out-Null

# Jobs: (display name, URL, cached file name)
$jobs = @(
    [pscustomobject]@{ Name = 'AI4';     Url = "$githubBase/SASPro_Models_AI4.zip";     File = 'SASPro_Models_AI4.zip' }
)
if (-not $NoWalking) {
    $jobs += [pscustomobject]@{ Name = 'Walking'; Url = "$githubBase/SASPro_Models_Walking.zip"; File = 'SASPro_Models_Walking.zip' }
}

function Download-Job {
    param([Parameter(Mandatory)] $Job)
    $dst = Join-Path $CacheDir $Job.File

    # Probe total size from HEAD so we can decide skip / resume.
    $head = Invoke-WebRequest -Uri $Job.Url -Method Head -ErrorAction Stop
    $total = [int64]$head.Headers['Content-Length'][0]

    if ((Test-Path $dst) -and -not $Force -and (Get-Item $dst).Length -eq $total) {
        Write-Host ("  cached: {0} ({1:N0} MB)" -f $Job.File, ($total / 1MB)) -ForegroundColor DarkGray
        return
    }

    if ($Force -and (Test-Path $dst)) { Remove-Item $dst -Force }

    Write-Host ("  downloading {0} ({1:N0} MB) ..." -f $Job.File, ($total / 1MB)) -ForegroundColor Cyan
    # -Resume picks up a partial .zip if the previous run was interrupted.
    Invoke-WebRequest -Uri $Job.Url -OutFile $dst -Resume -ErrorAction Stop
    $got = (Get-Item $dst).Length
    if ($got -ne $total) {
        throw ("Download size mismatch: expected {0:N0}, got {1:N0}" -f $total, $got)
    }
    Write-Host ("  done: {0}" -f $dst) -ForegroundColor Green
}

function Extract-Job {
    param([Parameter(Mandatory)] $Job)
    $zip = Join-Path $CacheDir $Job.File
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("saspro_models_extract_{0}_{1}" -f $PID, [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        Write-Host ("  extracting {0} ..." -f $Job.File) -ForegroundColor Cyan
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force

        # Strip a single top-level folder if present (matches install_models_zip()).
        $root = $tmp
        $kids = Get-ChildItem -LiteralPath $tmp
        if ($kids.Count -eq 1 -and $kids[0].PSIsContainer) { $root = $kids[0].FullName }

        # Overlay-copy into $OutputDir; never delete existing files (so SyQon /
        # Axiom / Aberration AI weights survive).
        $count = 0
        Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
            $rel = [System.IO.Path]::GetRelativePath($root, $_.FullName)
            $target = Join-Path $OutputDir $rel
            $targetDir = Split-Path -Parent $target
            if ($targetDir) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            $count++
        }
        Write-Host ("  installed {0} files -> {1}" -f $count, $OutputDir) -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "[1/2] Downloading model zips" -ForegroundColor Cyan
Write-Host ("  cache: {0}" -f $CacheDir) -ForegroundColor DarkGray
foreach ($job in $jobs) { Download-Job $job }

if ($NoExtract) {
    Write-Host "Done (download only)." -ForegroundColor Green
    return
}

Write-Host "[2/2] Extracting into $OutputDir" -ForegroundColor Cyan
foreach ($job in $jobs) { Extract-Job $job }

Write-Host ""
Write-Host "Done. Models in: $OutputDir" -ForegroundColor Green
