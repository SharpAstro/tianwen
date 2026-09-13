# P2 E3.4b: the pool arm. E3.2's recipe on a cache whose training pool holds the eight held-out SH61
# EDPH nights beside E2's eight, so the prior has seen the star PROFILE of the optics it is read on.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 beside E3.4a; runs after it, on the GPU alone.
#
# WHY. The skirt column (the profile at 1 to 1.5 truth-FWHM against the truth's) reads 1.01 for the
#   E3.2 prior on the synthetic cache and 0.75 on the real hard crop round-tripped at 1.28x (0.64 on the
#   flattened centre window): the cache's truth IS the pool's own star profile, and the prior imposes
#   that profile on a frame whose sharp stars carry a broader skirt (the SH61 EDPH's halo under a 1.8 px
#   core). Star profile shape is a deployment coordinate beside width, and the pool held two SV605CC /
#   SH61 nights of ten. E3.4a tests the other mechanism (the upsampled input's sampling) first; this
#   arm tests the pool, and the two are not exclusive.
#
# WHAT IT IS. E3.2's recipe unchanged (RL K = 20, the residual prior after every iteration, arm B's
#   objective plus the star term at 1.84e-3 with its empty counterpart, --scale-aug 0.7,1.0) on the
#   cache n2n-p2-blur-clamped-sh61 (arms/e34b-train-16.txt; validation pair unchanged, so the gate rows
#   compare). The Statue night (both targets) stays out of the pool. One seed.
#
# PREDICTION. On the real hard crop round-tripped at 1.28x (e34b_s0_final.pt): skirt 0.85 or over
#   (from 0.75) at width 1.15 or under, stars 0.85 to 1.10, ring depth within 1 MAD; as shot, no worse
#   than E3.2's 1.201 / 1.18. Cache gate within 0.03 of E3.2's 1.033 (the validation pair is the same;
#   more pool should not cost it). Nebula crop band residual under E3.2's 0.90. Confidence MODERATE:
#   the profile mechanism is inferred from one pair, and eight nights of one scope may still not carry
#   its sharp-night profile, all eight being at or over 2.05 px in the gate's statistic.
# PRIMARY READOUT: C:/temp/e2/e3-4b-s0-real-statue.txt beside e3-4a-s0-real-statue.txt.
# KILL LINE: round-tripped skirt still under 0.80, or width over 1.13 with the skirt within 0.03 of
#   0.75. Then the pool's profile is not what the prior lacks either, and the skirt has to be asked
#   for directly: E3.4c, a skirt term against the truth's profile in the objective, judged on the same
#   readout, with the caveat already recorded that the cache cannot see the deficit it would train on.
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0),
    [int]$K = 20,
    [string]$Weight = '1.84e-3',
    [string]$ScaleAug = '0.7,1.0'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-4b.status'
$log = Join-Path $LogDir 'e3-4b.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-4b'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-4b.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped-sh61'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.4b: E3.2's recipe on the SH61-augmented pool ($cache), --scale-aug $ScaleAug" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e34b_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.4b seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.4b seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.4b seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
