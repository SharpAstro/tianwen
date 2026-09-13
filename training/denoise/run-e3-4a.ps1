# P2 E3.4a: the prior trained on tiles UPSAMPLED 1.0 to 1.4x, so it meets in training the frame the
# 1.28x runtime path hands it.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's "Corrected the same evening".
#
# WHY. The user's goal: tighter stars WITHOUT taking their skirt away, more non-stellar detail, ringing
#   at a minimum. Measured on the real Statue pair with the new skirt column (the profile at 1 to 1.5
#   truth-FWHM against the truth's): the E3.2 prior at native scale reads skirt 1.18 (no deficit, but
#   width 1.201, hardly tighter), and round-tripped at 1.28x reads width 1.101 with skirt 0.75 and, on
#   the flattened centre window, 0.64 with 18 percent more detections than the truth. E3.0 on the same
#   round trip reads 0.95 at width 1.127. So the skirt is lost on the ZOOMED path, and lost more by
#   the prior than by the physics. The prior trained on native and DOWNSAMPLED tiles (E3.2: 0.7 to
#   1.0) and was then handed a bicubic-UPSAMPLED frame: smoother skirts and pixel-correlated noise it
#   had never seen, which it reads as blur to remove. On the synthetic cache its skirt is 1.01, so an
#   objective term there has nothing to correct: the mismatch is the input's sampling, not the loss.
#
# WHAT IT IS. E3.2's recipe (RL K = 20, zero-initialised residual prior after every iteration, arm B's
#   objective plus the star term at 1.84e-3 with its empty counterpart, the E2.6 pool's clamped cache)
#   with --scale-aug 1.0,1.4: each batch resampled UP by one factor in that range (bicubic, sides a
#   multiple of 16, the kernel label and star positions scaled with it), so the training truths span
#   2.1 to 3.6 px in the pool's native statistic and the inputs carry the upsampled sampling the runtime
#   path produces. One seed. Deployment: the 1.28x round trip, as for E3.2.
#
# PREDICTION. On the real Statue hard crop (n2n_operator_real.py --crop 0,1024,1024 --zoom 1.285
#   --roundtrip, checkpoint e34a_s0_final.pt): skirt 0.90 or over (from E3.2's 0.75) with width at or
#   under 1.15 (E3.2: 1.101), stars 0.85 to 1.10 and the ring depth excess within 1 MAD either way;
#   on the nebula crop (--crop 1256,1323,512, round-tripped) the star-masked BAND RESIDUAL to the truth
#   (n2n_star_shape.detail_ratio's second value, 1e-3 stretched units) reads under E3.2's 0.90, toward
#   the input's 0.479 (E3.0 reads 0.79; both arms with the frame-wide kernel move the nebula AWAY from
#   the truth on this crop, recorded before launch in e32-s0-real-statue-detail.txt; the band
#   correlation reads 0.99 for every arm and is not a readout). Secondary, recorded not judged: the
#   cache gate's
#   truths (2.07 to 2.56 px) now sit at the LOW edge of the band, so the gate row may read a wider
#   width than E3.2's 1.033; as shot (no zoom) the real crop reads no better than E3.2's 1.201.
#   Confidence MODERATE: one mechanism (sampling mismatch) is inferred from one comparison (native
#   1.18 against round-trip 0.75) on one pair.
# PRIMARY READOUT: C:/temp/e2/e3-4a-s0-real-statue.txt (hard crop as shot and round-tripped, nebula
#   crop round-tripped) beside e32-s0-real-statue-skirt.txt.
# KILL LINE: round-tripped skirt still under 0.85, or width over 1.13 with the skirt unchanged within
#   0.03 of 0.75. Then the sampling mismatch is not what takes the skirt, and the next arm is the pool
#   (E3.4b: the seven held-out SH61 EDPH nights added to the training pool, the Statue pair still held
#   out), a data arm with the same readout.
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0),
    [int]$K = 20,
    [string]$Weight = '1.84e-3',
    [string]$ScaleAug = '1.0,1.4'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e3-4a.status'
$log = Join-Path $LogDir 'e3-4a.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e3-4a'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-4a.ps1 $snap -Force
$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) { throw "no $f at $cache" }
}
try {
    "=== E3.4a: E3.2's recipe with --scale-aug $ScaleAug (upsampled tiles, the runtime path's sampling)" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e34a_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train E3.4a seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append; continue }
        "train E3.4a seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --scale-aug $ScaleAug --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.4a seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
