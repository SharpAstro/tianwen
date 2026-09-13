# E3.4b prepare: a second operator cache from the SAME blur export, with the eight held-out SH61 EDPH
# nights added to the training pool (arms/e34b-train-16.txt); the validation pair unchanged, so the
# gate rows stay comparable with E3.1 to E3.4a. CPU and disk only; may run while a seed trains.
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = 'D:\Astro-Dataset\degraded\p2-blur-clamped',
    [string]$Bake = 'D:\Astro-Dataset\2026-09-12-clamped',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-4b-prepare.status'
$log = Join-Path $LogDir 'e3-4b-prepare.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-4b-prepare'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-4b-prepare.ps1 $snap -Force
Copy-Item arms\e34b-train-16.txt, arms\e2-val-2.txt $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped-sh61'
if (Test-Path (Join-Path $cache 'meta.json')) {
    throw "a cache already exists at $cache; this script never overwrites one (delete it deliberately first)"
}
try {
    "=== E3.4b prepare: $Export -> $cache (bake $Bake) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    & python -u n2n_smoke.py --prepare --root $Export --bake $Bake --cache $cache `
        --train-from-list arms\e34b-train-16.txt --val-from-list arms\e2-val-2.txt `
        --cells-per-session 45 --val-cells-per-session 120 *>> $log
    if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
