# P2 E3.4c: the augmentation STRADDLES 1.0 (0.6 to 1.4), so the training median stays at the pool's own
# width while half the draws carry the upsampled sampling the runtime path produces; read at 1.42x.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-14 00:50 from E3.4a's reading.
#
# WHY. E3.4a (tiles upsampled 1.0 to 1.4x) proved both mechanisms at once and set them against each
#   other: seeing upsampled inputs restored the skirt on the zoomed path (E3.2's 0.75 became 1.29 at
#   1.28x and 1.03 at 1.42x), and moving the training median to about 2.7 px moved the width floor up
#   with it, so the arm returns the input as shot (1.304 from 1.253) and tightens only where the zoom
#   lifts the stars toward its median (1.214 / 1.163 / 1.114 at 1.28x / 1.42x / 1.6x). E3.2 (0.7 to 1.0,
#   median 1.93 px) tightened at 1.28x and skirted. The floor follows the training median (E3.1 to
#   E3.4a, four points); the skirt follows whether the prior has met the deployment's sampling.
#
# WHAT IT IS. E3.2's recipe with --scale-aug 0.6,1.4 on the E2.6 pool's clamped cache: median factor
#   1.0 (truths about 1.36 to 3.6 px, median near the pool's 2.27), draws over 1.0 upsampled as the
#   runtime path does. One seed. Deployment: the 1.42x round trip (truth 2.58 px on the hard crop,
#   above a floor expected near 2.5).
#
# PREDICTION. On the real hard crop round-tripped at 1.42x (e34c_s0_final.pt): width at or under 1.15
#   with skirt 0.90 or over, stars 0.85 to 1.10, the annulus minimum not below the input's (ring depth
#   excess at or under +1 MAD; a negative value is sky flattening and passes). At 1.28x: skirt at or
#   over 0.85 (E3.2 0.75). Nebula crop at 1.42x: band residual under E3.2's 0.90. Cache gate: within
#   0.05 of E3.2's 1.033 (the median is back near the pool's). Confidence MODERATE: the two mechanisms
#   are each read from one arm, and their combination is a prediction, not a measurement.
# PRIMARY READOUT: C:/temp/e2/e3-4c-s0-real-statue.txt beside e3-4a-s0-real-statue.txt.
# KILL LINE: at 1.42x width over 1.15 AND skirt under 0.90 (neither clause met), or a pass by
#   fabrication (stars over 1.10). Then the two mechanisms cannot be had from one prior at one scale
#   and the runtime path is E3.0 with per-window kernels, or a blend.
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
$status = Join-Path $LogDir 'e3-4c.status'
$log = Join-Path $LogDir 'e3-4c.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-4c'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-4c.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.4c: E3.2's recipe with --scale-aug $ScaleAug (straddling 1.0; read at 1.42x)" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e34c_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.4c seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.4c seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.4c seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
