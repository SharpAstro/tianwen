# P2 E2.6: the power check, before E3 spends a day on four arms of three seeds.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the export. Written 2026-09-06.
#
# WHY. E3 is planned as four arms x three seeds. That is the exact shape the DENOISER campaign already
#   paid for: on its E2 the between-regime sd was 0.20 / 0.28 / 0.61 against a within-regime (seed) sd
#   of 0.58 / 0.82 / 1.40, so three-seed arms could not read the one-point effects being chased and
#   about 31 seeds would have been needed. Nobody has measured whether the DECONVOLVER's metrics behave
#   the same way, and they might not: fwhm_ratio is a geometric quantity with a hard floor at 1.0,
#   which is a different animal from a noise ratio. So this measures the seed spread FIRST, on one arm,
#   and E3's seed count is set from it.
#
# WHAT IT IS. Six seeds of ONE arm, identical in every respect but the seed: the recipe E3's control
#   would use (shared kernel, estimator label, noise after blur, two bands). No comparison is made here
#   and no hypothesis is tested; the output is a spread.
#
# PREDICTION: the seed sd of the selected checkpoint's fwhm_ratio is under 0.05, which would make a
#   0.1x effect readable at three seeds and E3 runnable as planned. Confidence: LOW. The denoiser's
#   experience points the other way, and the deconvolution gate has a hard floor at 1.0 that could
#   either compress the spread (every seed pinned near the floor) or inflate it (seeds that cross it
#   are rejected outright, so the surviving sample is truncated). Both are possible and neither has
#   been seen.
# WHAT WOULD CHANGE THE PLAN: a seed sd above 0.05 means E3's arms need more than three seeds, and the
#   number needed is computed from this spread rather than guessed. A seed sd above 0.15 means the
#   metric cannot rank arms at any affordable seed count and E3 needs a different readout before it
#   runs at all, which is worth knowing for an hour of GPU rather than a day.
# NOT A KILL. This is a measurement; there is no arm to kill.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-p2-power.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Bake = 'D:\Astro-Dataset\2026-09-full',
    [string]$Export = 'D:\Astro-Dataset\degraded\p2-blur',
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2, 3, 4, 5)
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 3,4,5' arrives as the single string '3,4,5' and coerces to 345; use -Command instead."
    }
}

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'p2-power.status'
$log = Join-Path $LogDir 'p2-power.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-p2-power'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-p2-power.ps1 $snap -Force
Copy-Item arms\e2-train-8.txt, arms\e2-val-2.txt $snap -Force

try {
    # The SAME eight train and two val sessions E2 used, rather than a fresh selection. Continuity is
    # the point: the denoiser's seed spread was measured on these, so a difference between the two
    # campaigns' spreads is about the METRIC and not about which nights were drawn.
    if (-not (Test-Path (Join-Path $Export 'degradations.jsonl'))) {
        "export $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & dotnet run --project ..\..\src\TianWen.Cli -c Release -- dataset degrade `
            --bake $Bake --out $Export --mode blur --draws 8 --cells 45 --seed 1 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "degrade failed" }
    }

    $cache = Join-Path $Scratch 'n2n-p2-blur'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
        "prepare $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\e2-train-8.txt --val-from-list arms\e2-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed" }
    }

    foreach ($seed in $Seeds) {
        $out = "p2pow_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) {
            "train seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        "train seed $seed -> $out" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --cond-psf01 --loss l2 --upsample `
            --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 `
            --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for seed $seed" }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
