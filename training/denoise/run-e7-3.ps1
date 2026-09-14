# P2 E7.3: the E3.4d recipe with the operator's kernel LABEL jittered a quarter either way against the
# kernel that made the tile, so the prior learns to tolerate a kernel read wrong.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-14 17:10 from E7.1 and E7.2's readings.
#
# WHY. E7.1 swept the pair's kernel through the E3.4d prior at 0.5x to 2.0x on the primary crop at the
#   1.28x round trip: every clause follows the kernel monotonically and the skirt steeply (1.81 / 1.48 /
#   0.98 / 0.53 / 0.12 / -0.46), so the tolerated band is a TENTH either way, not the quarter predicted.
#   No single-frame rule lands in it: the training draws' fraction reads 1.17 to 1.31x the pair's kernel,
#   the pool-floor rule refuses the frame, and E7.2's learned head misses on two seeds (0.88 / 1.30 /
#   0.93 and 0.67 / 0.67 / 0.58 of est-c) and reads a resampled frame 1.2 to 2.1x too blurred. Width
#   alone cannot see excess blur, and no reading is good to a tenth; so the prior must stop needing one.
#
# WHAT IT IS. E3.4d's line exactly (n2n-p2-blur-clamped-sh61, RL K = 20, the residual prior, arm B's
#   objective plus the star term at 1.84e-3 with its empty counterpart, --scale-aug 0.6,1.4) plus
#   --kernel-jitter 0.25: the RL steps run with the jittered label, the truth is unchanged. One seed.
#
# PREDICTION. On the primary crop at 1.28x with the pair's kernel scaled by 0.8 and by 1.25 (the E7.1
#   sweep's own settings), every star clause holds: width at or under 1.15, stars 0.85 to 1.10, ring
#   within 1 MAD, skirt 0.90 or over; and at the pair's own kernel the E3.4d result is kept within 0.02
#   on width (1.146) and skirt (0.98). Confidence MODERATE: the prior has the tile and the truth to learn
#   the read from, and the pixel loss is a stronger teacher than E7.2's relative width loss was.
# PRIMARY READOUT: C:/temp/e2/e7-3-s0-real-statue.txt (the sweep's three scales 0.8 / 1.0 / 1.25 at
#   1.285 round trip) beside C:/temp/e2/e7-1/sens-*.txt.
# KILL LINE: at the pair's kernel the width reads over 1.17 (the tolerance was bought by doing less),
#   or at 1.25x the skirt is still under 0.80 (the jitter did not teach the read). Then E7 goes to
#   self-calibration on the output's own star profile.
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0),
    [int]$K = 20,
    [string]$Weight = '1.84e-3',
    [string]$ScaleAug = '0.6,1.4',
    [double]$Jitter = 0.25
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e7-3.status'
$log = Join-Path $LogDir 'e7-3.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e7-3'
New-Item -ItemType Directory -Force $snap | Out-Null
if ($PSScriptRoot -ne $snap) { Copy-Item *.py, run-e7-3.ps1 $snap -Force }
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped-sh61'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E7.3: E3.4d's recipe on $cache with --scale-aug $ScaleAug and --kernel-jitter $Jitter" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e73_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E7.3 seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E7.3 seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --kernel-jitter $Jitter --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E7.3 seed $seed" }
        # The primary readout: the pair's kernel scaled 0.8 / 1.0 / 1.25 at the 1.285 round trip.
        $readout = Join-Path $LogDir "e7-3-s$seed-real-statue.txt"
        foreach ($scale in 0.8, 1.0, 1.25) {
            $kernels = (0.77, 0.91, 0.98 | ForEach-Object { '{0:F4}' -f ($_ * $scale) }) -join ','
            "################ crop 0,1024,1024 1.285 kernel x$scale ($kernels)" | Out-File $readout -Append -Encoding utf8
            & python n2n_operator_real.py --cache $cache --checkpoint $out --crop 0,1024,1024 --zoom 1.285 --roundtrip --kernels $kernels *>> $readout
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
