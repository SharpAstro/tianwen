# P2 E2.8c: arm N at a FIXED star-term weight. Was seed 1 the weight, or the seed?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-07 from the plan's E2.8b results section.
#
# WHY. E2.8b's arm N (the star term plus its counterpart over EMPTY target windows) did what it was
#   built for on the observer session: fabrication 34 to 45x the truth's stars down to 2.6 to 9.7x,
#   four of five seeds under the input's own null of 6.9. Four of five seeds paid with the sharpening
#   (selected 1.32 to 1.34 against an input of 1.38, stars 0.63, final widths at or past the input:
#   trained toward the identity). Seed 1 kept both: 1.183 selected at step 3700 holding 0.75, observer
#   9.7x at width 0.975. It drew the arm's LOWEST weight, 1.8386e-03 (the other four 3.4e-3 to 7.0e-3),
#   and the weight is matched on the FIRST batch, so it is a random variable of the seed. One seed is a
#   lead, not a result; this arm rotates the axis it rides on.
#
# WHAT IT IS. Arm N's recipe with --star-loss 1.84e-3 (seed 1's value, fixed from step 1 on every
#   seed) instead of --star-loss auto. Seeds 0 to 4, same cache, gate and observer as E2.8 and E2.8b;
#   arm N's five runs (p2starN_s*.pt, p2-star-b.log) are the paired control, and arm N seed 1 is the
#   run this arm should reproduce five times if the weight is the cause.
#
# PREDICTION. At least three of five seeds select at or under 1.25 on the gate session while the
#   observer stays under 2x its null (14) and over 1.0 in width; the width minima hold at least 0.65
#   of the stars. Confidence MODERATE: a fixed weight removes one random variable and leaves the
#   seed's own, which E2.7 measured at 0.008 to 0.031 on width.
# PRIMARY READOUT: n2n_gatelog.py C:/temp/e2/p2-star-c.log, the selected out/truth and the observer
#   columns side by side.
# KILL LINE: at most one of five seeds selects under 1.30 with the observer under 14. Then seed 1 was
#   the seed and not the weight, and the counterpart goes into the unrolled operator as a REGULARISER
#   only, never as the sharpening objective of a pixel-domain net.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-p2-starloss-c.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2, 3, 4),
    [string]$Weight = '1.84e-3'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 0,1,2' arrives as the single string '0,1,2' and coerces to 12; use -Command instead."
    }
}

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'p2-star-c.status'
$log = Join-Path $LogDir 'p2-star-c.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-p2-star-c'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-p2-starloss-c.ps1 $snap -Force

$cache = Join-Path $Scratch 'n2n-p2-blur'
if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
    throw "no cache at $cache. E2.6 built it; this arm deliberately does not rebuild it."
}
if (-not (Test-Path (Join-Path $cache 'empty.npy'))) {
    throw "no empty.npy at $cache. E2.8b's --prepare-stars wrote it; this arm deliberately does not redraw it."
}

try {
    "=== arm C: arm N with --star-loss $Weight fixed from step 1 (seed 1 of arm N drew 1.8386e-03)" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "p2starC_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) {
            "train arm C seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        "train arm C seed $seed -> $out" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --cond-psf01 --loss l2 --upsample `
            --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for arm C seed $seed" }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
