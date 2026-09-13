# P2 E3.4d: both mechanisms at once. E3.2's recipe on the SH61-augmented pool (E3.4b's cache) with the
# augmentation straddling 1.0 (E3.4c's --scale-aug 0.6,1.4), read at 1.28x and 1.42x.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-14 02:55 from E3.4b's reading; runs after E3.4c.
#
# WHY. E3.4b (the pool arm) PASSED its pre-registration on the real hard crop round-tripped at 1.28x:
#   width 1.119, stars 1.04, ring depth excess +0.07, skirt 0.87 (E3.2: 1.101 / 1.03 / -0.8 / 0.75).
#   The pool's star profile was the larger lever on the skirt at a held width. E3.4a (upsampled tiles)
#   gave the skirt back further (1.29 at 1.28x, 1.03 at 1.42x) at the cost of the width, because its
#   training median moved to 2.7 px. E3.4c straddles 1.0 on the OLD pool to keep the median and add
#   the sampling; this arm does the same on the SH61 pool. The user's goal asks skirt 0.9 to 1.1 with
#   the tightening kept; E3.4b is 0.03 short on the skirt and E3.4a 0.06 short on the width at 1.42x.
#
# WHAT IT IS. n2n-p2-blur-clamped-sh61 (arms/e34b-train-16.txt; validation pair unchanged), RL K = 20,
#   the residual prior, arm B's objective plus the star term at 1.84e-3 with its empty counterpart,
#   --scale-aug 0.6,1.4. One seed. Deployment: the round trip at 1.28x or 1.42x, whichever reads.
#
# PREDICTION. On the real hard crop (e34d_s0_final.pt), at 1.28x OR 1.42x: width at or under 1.15
#   with skirt 0.90 or over (the GOAL's line), stars 0.85 to 1.10, the annulus minimum not below the
#   input's (ring depth excess at or under +1). Nebula crop band residual under E3.4b's 0.784. Cache
#   gate within 0.05 of E3.4b's 1.046. Confidence MODERATE: the two levers were each read from one
#   arm and their sum is a prediction.
# PRIMARY READOUT: C:/temp/e2/e3-4d-s0-real-statue.txt beside e3-4b-s0-real-statue.txt.
# KILL LINE: skirt under E3.4b's 0.87 at a width within 0.02 of E3.4b's 1.119 at 1.28x (the
#   augmentation costs what the pool bought), or a pass by fabrication (stars over 1.10). Then the
#   shipping candidate is E3.4b's checkpoint at 1.28x, and the skirt's last 0.03 is asked of E7's
#   blend dial.
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0),
    [int]$K = 20,
    [string]$Weight = '1.84e-3',
    [string]$ScaleAug = '0.6,1.4'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-4d.status'
$log = Join-Path $LogDir 'e3-4d.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-4d'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-4d.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped-sh61'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.4d: E3.2's recipe on the SH61 pool ($cache) with --scale-aug $ScaleAug" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e34d_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.4d seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.4d seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.4d seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
