# P2 E3.3: the scale augmentation moved DOWN, so the training truths' MEDIAN width sits under the
# real frames' width rather than over it.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's "E3.2 read on the real pair".
#
# WHY. E3.2 (--scale-aug 0.7,1.0, truths 1.45 to 2.56 px, median about 1.93) moved the prior's floor but
#   not far enough: on the real Statue crop as shot (truth 1.813 px) it reads 1.222 / 1.201 (selected /
#   final) against E3.1's 1.259 and E3.0's 1.163, stars kept, no ringing; the same crop zoomed reads
#   1.100 at 2.16 px, 1.043 at 2.39 px and 0.996 at 2.62 px, beating E3.0 from 2.16 px up. Two
#   coordinates were excluded on the zoomed crop: white noise added up to twice the input's MAD moves
#   the row by 0.001, and the kernel label widened 1.3x to 1.6x as shot moves it from 1.20 to 1.12 with
#   the ring turning positive, so the prior is clamped by the truth's WIDTH and by nothing else measured.
#   E3.1's floor sat near 2.5 px over a training median of 2.27; E3.2's near 2.1 over a median of 1.93:
#   the floor follows the MEDIAN of the training truths, at about 1.1 times it, not their minimum.
#
# WHAT IT IS. E3.2's recipe with --scale-aug 0.55,0.85: truths from about 1.14 to 2.18 px, median about
#   1.59, so 1.8 px real frames sit ABOVE the median. One seed. The GPU is the training's alone.
#
# PREDICTION. On the real Statue crop as shot (n2n_operator_real.py --checkpoint e33_s0.pt, crop
#   0,1024,1024, no zoom): out/truth at or under 1.15 with stars at or over 0.85 and a non-positive ring
#   excess, i.e. the E3.2 round-trip row (1.101 at zoom 1.28) without the zoom. Secondary: the cache
#   gate's truths (2.07 to 2.56 px) now lie ABOVE the band, so the gate row is expected WORSE than
#   E3.2's 1.024 (prediction: 1.03 to 1.10, stars 0.85 to 0.95) and the observer's fabrication may
#   rise; both are recorded, neither is the readout. Confidence MODERATE: the floor's driver is
#   inferred from two points.
# PRIMARY READOUT: C:/temp/e2/e3-3-s0-real-statue.txt (three crops as shot) beside e32-s0-real-statue.txt.
# KILL LINE: the crop as shot still reads 1.19 or over (the floor did not follow the median down), or
#   passes only by fabricating (stars over 1.10 at a width under the truth's). Then the floor is not
#   set by the median width and the runtime rule is E3.2's zoom round trip (deconvolve at 1.3x to
#   1.4x, resample back), which clears the bar today (1.101 / 1.052 at zoom 1.28 / 1.42).
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0),
    [int]$K = 20,
    [string]$Weight = '1.84e-3',
    [string]$ScaleAug = '0.55,0.85'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-3.status'
$log = Join-Path $LogDir 'e3-3.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-3'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-3.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.3: E3.2's recipe with --scale-aug $ScaleAug" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e33_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.3 seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.3 seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.3 seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
