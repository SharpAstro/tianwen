# E3: prepare the trainer's cache from the CLAMPED degradation export (task 1 of the E3 chain).
#
# ---------------------------------------------------------------------------------------------------
# WHAT IT IS. `--prepare` over D:/Astro-Dataset/degraded/p2-blur-clamped (E2.6's pool re-exported from
#   the 2026-09-12-clamped store with --estimate-kernels, 78 sessions, landed 2026-09-13 09:50) into a
#   NEW scratch cache. The train and val sessions are pinned BY NAME to the same lists E2.6 to E2.10
#   used (arms/e2-train-8.txt, arms/e2-val-2.txt), so nothing is redrawn; --bake names the store the
#   retained masters live in, which is what lets the operator deconvolve in LINEAR units: each
#   session's stretch parameters are recomputed from its master and PROVED against the export's own
#   clean tiles before stretch.npy is written (n2n_operator's docstring says why).
#
# WHAT CHANGES AGAINST n2n-p2-blur. Every master is re-stacked (both registration fixes, clamped
#   Lanczos-3 on the staged sessions, drizzle pixfrac 1 on the OSC ones), so every tile is new bytes;
#   the Tarantula 2025-10-14 train session is now an 84-frame drizzle master where it was a 57-frame
#   staged one. The cache additionally carries kernels.npy (the row's estimated kernel per degraded
#   slot) and stretch.npy (the session's MTF parameters per cell). Nothing else about the layout moves.
#
# Run detached; read the status file, never the log while it runs:
#   $s = "$PWD\run-e3-prepare.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = 'D:\Astro-Dataset\degraded\p2-blur-clamped',
    [string]$Bake = 'D:\Astro-Dataset\2026-09-12-clamped',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-prepare.status'
$log = Join-Path $LogDir 'e3-prepare.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-prepare'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-prepare.ps1 $snap -Force
Copy-Item arms\e2-train-8.txt, arms\e2-val-2.txt $snap -Force

$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
if (Test-Path (Join-Path $cache 'meta.json')) {
    throw "a cache already exists at $cache; this script never overwrites one (delete it deliberately first)"
}

try {
    "=== E3 prepare: $Export -> $cache (bake $Bake) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    & python -u n2n_smoke.py --prepare --root $Export --bake $Bake --cache $cache `
        --train-from-list arms\e2-train-8.txt --val-from-list arms\e2-val-2.txt `
        --cells-per-session 45 --val-cells-per-session 120 *>> $log
    if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
