# Fetch + materialize TianWen AI models (AI4, Walking Noise) into
# %LOCALAPPDATA%\TianWen\models.
#
# Sourcing strategy:
#   1. Probe %LOCALAPPDATA%\SASpro\models. If SetiAstroSuite Pro has a file we
#      need, hardlink it into TianWen's tree -- zero disk cost on NTFS, the
#      bytes survive even if SAS Pro is later uninstalled (last hardlink keeps
#      the inode alive). Falls back to plain copy when the hardlink fails
#      (cross-volume, non-NTFS, ReFS without hardlink support, etc.).
#   2. For files SAS Pro does NOT have, download the upstream zip
#      (github.com/setiastro/setiastrosuitepro releases, tag benchmarkFIT) into
#      a cache dir and extract only the missing entries.
#   3. Idempotent: files already present under TianWen\models are skipped, so
#      re-runs are safe and cheap.
#
# When to run this:
#   - First-time TianWen install where we want AI4 / Walking Noise models.
#   - After clearing %LOCALAPPDATA%\TianWen\models to re-materialize from scratch.
#   - On CI before running AI enhancement tests (use -NoDownload to fail
#     loudly if SAS Pro isn't pre-staged, or omit it to pull from GitHub).
#
# Usage:
#   pwsh tools/tianwen-ai-models-fetch.ps1
#   pwsh tools/tianwen-ai-models-fetch.ps1 -NoWalking
#   pwsh tools/tianwen-ai-models-fetch.ps1 -OutputDir D:\my\models
#   pwsh tools/tianwen-ai-models-fetch.ps1 -NoSasPro          # ignore SAS Pro source
#   pwsh tools/tianwen-ai-models-fetch.ps1 -NoDownload        # SAS Pro only; fail on miss
#   pwsh tools/tianwen-ai-models-fetch.ps1 -PruneCache        # delete cached zips after
#   pwsh tools/tianwen-ai-models-fetch.ps1 -Force             # redownload cached zips
#
# Notes:
#   - v1 always needs the zip cached (locally) so we can read its central
#     directory and know the expected file set. -NoDownload therefore requires
#     a previous run to have populated the cache. A future revision could ship
#     a tools/saspro-models-manifest.json to lift this requirement.
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$SasProDir,
    [string]$CacheDir,
    [switch]$NoWalking,
    [switch]$NoSasPro,
    [switch]$NoDownload,
    [switch]$PruneCache,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Suppress Invoke-WebRequest's default progress bar; it stalls pwsh on multi-GB files.
$ProgressPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$githubBase = 'https://github.com/setiastro/setiastrosuitepro/releases/download/benchmarkFIT'

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

function Get-DefaultSasProDir {
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

if (-not $OutputDir) { $OutputDir = Get-DefaultOutputDir }
if (-not $SasProDir) { $SasProDir = Get-DefaultSasProDir }
if (-not $CacheDir)  { $CacheDir  = Join-Path $HOME '.cache/tianwen-ai-models' }

New-Item -ItemType Directory -Path $CacheDir  -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$useSasPro = -not $NoSasPro -and (Test-Path -LiteralPath $SasProDir)
if ($useSasPro) {
    Write-Host ("SAS Pro source: {0}" -f $SasProDir) -ForegroundColor DarkGray
} elseif ($NoSasPro) {
    Write-Host "SAS Pro source: ignored (-NoSasPro)" -ForegroundColor DarkGray
} else {
    Write-Host ("SAS Pro source: not found at {0} (will extract from zip)" -f $SasProDir) -ForegroundColor DarkGray
}
Write-Host ("Output:         {0}" -f $OutputDir) -ForegroundColor DarkGray

# Jobs: (display name, URL, cached file name)
$jobs = @(
    [pscustomobject]@{ Name = 'AI4';     Url = "$githubBase/SASPro_Models_AI4.zip";     File = 'SASPro_Models_AI4.zip' }
)
if (-not $NoWalking) {
    $jobs += [pscustomobject]@{ Name = 'Walking'; Url = "$githubBase/SASPro_Models_AI4_walking.zip"; File = 'SASPro_Models_AI4_walking.zip' }
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

function Compute-StripPrefix {
    # If every file entry sits under a single common top-level folder, return
    # "<folder>/" so callers can strip it. Else return "". Matches the
    # install_models_zip() heuristic from SetiAstroSuite Pro, which keeps our
    # in-tree relative paths aligned with what SAS Pro itself produces on disk.
    param([Parameter(Mandatory)] $Entries)
    $tops = @{}
    foreach ($e in $Entries) {
        $parts = $e.FullName -split '[\\/]', 2
        if ($parts.Length -lt 2 -or [string]::IsNullOrEmpty($parts[1])) { return '' }
        $tops[$parts[0]] = $true
        if ($tops.Count -gt 1) { return '' }
    }
    if ($tops.Count -eq 1) { return ($tops.Keys | Select-Object -First 1) + '/' }
    return ''
}

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

function Materialize-Job {
    param([Parameter(Mandatory)] $Job)
    $zipPath = Join-Path $CacheDir $Job.File
    $haveZip = Test-Path -LiteralPath $zipPath

    if (-not $haveZip) {
        # v1 grounds the expected-file-set on the zip's central directory. No
        # cached zip means we cannot answer "what's missing from SAS Pro" with
        # certainty, so this is a hard failure rather than a silent partial.
        throw ("Cached zip missing for {0} and -NoDownload set. Run once without -NoDownload to populate the cache, or commit a manifest.json (see script header)." -f $Job.Name)
    }

    Write-Host ("  materializing {0} ..." -f $Job.Name) -ForegroundColor Cyan
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $files = $zip.Entries | Where-Object { $_.FullName -and -not $_.FullName.EndsWith('/') -and -not $_.FullName.EndsWith('\') }
        $strip = Compute-StripPrefix -Entries $files
        $stripLen = $strip.Length

        # ONNX-only: TianWen calls every model via Microsoft.ML.OnnxRuntime. The
        # bundles also ship .pth / .pt PyTorch weights (SAS Pro's torch fallback)
        # which we never load -- skip them outright so %LOCALAPPDATA%\TianWen\models
        # stays a clean .onnx + manifest.json tree, regardless of whether the
        # source is the cached zip or a SAS Pro hardlink.
        $files = $files | Where-Object {
            $ext = [System.IO.Path]::GetExtension($_.FullName).ToLowerInvariant()
            $ext -ne '.pth' -and $ext -ne '.pt'
        }

        $skipped = 0; $hardlinked = 0; $copied = 0; $extracted = 0
        $firstFallbackReason = $null
        foreach ($entry in $files) {
            $rel = $entry.FullName.Substring($stripLen) -replace '/', [System.IO.Path]::DirectorySeparatorChar
            $target = Join-Path $OutputDir $rel

            if (Test-Path -LiteralPath $target) {
                $skipped++
                continue
            }

            $targetDir = Split-Path -Parent $target
            if ($targetDir) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

            $sasprPath = if ($useSasPro) { Join-Path $SasProDir $rel } else { $null }
            if ($sasprPath -and (Test-Path -LiteralPath $sasprPath -PathType Leaf)) {
                if (Try-Hardlink -Source $sasprPath -Target $target) {
                    $hardlinked++
                } else {
                    Copy-Item -LiteralPath $sasprPath -Destination $target -Force
                    $copied++
                    if (-not $firstFallbackReason) {
                        # Re-run the link attempt without the swallow so we can
                        # report a concrete error to the user (most likely
                        # cross-volume between TianWen and SAS Pro on a non-C: drive).
                        try { New-Item -ItemType HardLink -Path "$target.linkprobe" -Value $sasprPath -ErrorAction Stop | Out-Null }
                        catch { $firstFallbackReason = $_.Exception.Message }
                        Remove-Item -LiteralPath "$target.linkprobe" -Force -ErrorAction SilentlyContinue
                    }
                }
            } else {
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
                $extracted++
            }
        }

        Write-Host ("    skipped:    {0,5} (already present)" -f $skipped)     -ForegroundColor DarkGray
        Write-Host ("    hardlinked: {0,5} (from SAS Pro)"     -f $hardlinked) -ForegroundColor Green
        if ($copied -gt 0) {
            Write-Host ("    copied:     {0,5} (hardlink failed: {1})" -f $copied, $firstFallbackReason) -ForegroundColor Yellow
        }
        Write-Host ("    extracted:  {0,5} (from cached zip)" -f $extracted)   -ForegroundColor Green
    } finally {
        $zip.Dispose()
    }
}

# Phase 1: download zips (unless -NoDownload).
if (-not $NoDownload) {
    Write-Host "[1/2] Downloading model zips" -ForegroundColor Cyan
    Write-Host ("  cache: {0}" -f $CacheDir) -ForegroundColor DarkGray
    foreach ($job in $jobs) { Download-Job $job }
} else {
    Write-Host "[1/2] Download skipped (-NoDownload)" -ForegroundColor DarkGray
}

# Phase 2: materialize into TianWen output dir.
Write-Host "[2/2] Materializing into $OutputDir" -ForegroundColor Cyan
foreach ($job in $jobs) { Materialize-Job $job }

# Phase 3: optional cache pruning. Hardlinks share inodes with SAS Pro's copy
# so the bytes survive zip removal; pure-extract files are already independent
# bytes on disk. Either way the cache is safe to drop once materialization
# completes successfully.
if ($PruneCache) {
    Write-Host "Pruning cache..." -ForegroundColor Cyan
    foreach ($job in $jobs) {
        $zipPath = Join-Path $CacheDir $job.File
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
            Write-Host ("  removed: {0}" -f $zipPath) -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
Write-Host "Done. Models in: $OutputDir" -ForegroundColor Green
