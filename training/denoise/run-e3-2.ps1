# P2 E3.2: the operator's prior with a SCALE AUGMENTATION, so its training truths span the widths it
# will meet.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's "Seed 0 on the REAL pair".
#
# WHY. E3.1 seed 0 sharpens the synthetic cache (gate 1.072, stars 0.88) and returns the INPUT on every
#   crop of the real Statue pair (1.259 from 1.253, 1.070 from 1.065, 1.085 from 1.068) while E3.0
#   alone deconvolves them (1.163, 0.994, 1.001). Measured cause: the real crops' truth stars are
#   1.81 to 1.93 px in the gate's statistic against training truths of 2.07 / 2.27 / 2.56 (p10 / p50
#   / p90), and the same crop resampled to 2.33 and 2.58 px has the prior sharpening and, at 2.58,
#   beating E3.0 (1.065, stars 1.04, ring -0.3). The archive offers no sharper pool: every retained
#   master but two reads 2.05 px or more in that statistic (C:/temp/e2/store-master-gate-widths.txt).
#
# WHAT IT IS. E3.1's recipe (RL K = 20, zero-initialised residual prior after every iteration, E2.7 arm
#   B's objective plus the star term with its empty counterpart at 1.84e-3, the E2.6 pool's cache)
#   with --scale-aug 0.7,1.0: each synthetic batch resampled by one random factor in that range
#   (bicubic, antialiased, side a multiple of 16), the kernel label and the star-term positions scaled
#   with it, so the truths the prior sees span about 1.45 to 2.7 px. One seed first; more only if the
#   real-frame readout passes. The GPU is the training's alone (E3.1 was stopped after seed 0 for it).
#
# PREDICTION. On the real Statue crop as shot (n2n_operator_real.py --checkpoint e32_s0.pt, crop
#   0,1024, no zoom): out/truth at or under 1.15 with stars at or over 0.85 and a non-positive ring
#   excess, the zoom-1.4 row without the zoom; on the cache gate, within 0.03 of seed 0's row (the
#   augmentation costs little inside the band). Confidence MODERATE: the augmentation reaches 1.45 px
#   only at the range's end and the sharpened-noise class the empty-window term flattens is narrower
#   still.
# PRIMARY READOUT: C:/temp/e2/e32-s0-real-statue.txt (the three crops) beside e31-s0-real-statue.txt.
# KILL LINE: the real crop as shot returns the input (out/truth within 0.03 of its 1.253), or passes
#   only by fabricating (stars over 1.10 at a width under the truth's). Then the floor is not the
#   width alone and the next measurement is which coordinate remains.
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
$status = Join-Path $LogDir 'e32.status'
$log = Join-Path $LogDir 'e32.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e32'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-2.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.2: E3.1's recipe with --scale-aug $ScaleAug" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e32_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.2 seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.2 seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.2 seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
