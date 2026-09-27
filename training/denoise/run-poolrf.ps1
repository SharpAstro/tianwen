# E14, the ring fix alone (docs/plans/denoiser-training.md, run log 2026-09-27): E13's `pool` arm again,
# byte for byte, on the pool re-exported after the canvas-ring fix (issue #489; run-pool-reexport.ps1,
# cache n2n-pool-rf). The first model trained on data that matches the fixed inference.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before any E14 checkpoint exists. Written 2026-09-27.
#
# WHY. Every model before the fix, the shipped one included, learned on exports stretched from a floor of 0
#   wherever a master carries an exact-zero canvas ring (108 of the 190 masters of 2026-09-25-full), while
#   inference now stretches from the covered pixels' own floor. n2n-pool and n2n-pool-rf hold the same 78
#   sessions, the same val pair and the same degrade flags and seed; only the stretch of a ringed master
#   differs. So `poolrf` against E13's `pool`, seed for seed, is what the fix buys in training.
#
# ARM. poolrf: base 32, --cond, E12's plateau recipe (cap 80000 steps), seeds 0 to 2, final weights, on
#   n2n-pool-rf. Nothing else changes.
#
# SCORED on the eleven E13 fields TWICE: on the existing eval caches (pre-fix exports, the domain every
#   earlier verdict was read in) and on ring-fixed re-exports of the same cells, built beside this run on
#   the CPU. Every model is re-scored in one pass: poolrf_s0..2, pool_s0..2, E12's conv_s0..3 and E10's
#   ctl4k_s0..5, with the fixed split. The ring-fixed caches are the primary read, because they are what
#   the product now feeds a model.
#
# PREDICTIONS (R and the frontier as E13):
#   (1) On the ring-fixed caches, poolrf's R is no lower than pool's beyond the E13 threshold (2.8 x pool's
#       seed sd x sqrt(2/3)), and its frontier is no worse on at least 8 of the 11 fields. Confidence:
#       moderate. KILL: poolrf below pool by more than that threshold, which would mean the fixed stretch
#       itself costs removal and has to be understood before any post-fix model ships.
#   (2) On the old caches, poolrf is no better than pool (it now trains in a domain those tiles are not
#       in). Confidence: low to moderate.
#   (3) The E13 redistribution is NOT the ring: poolrf keeps pool's per-field pattern (far more than E12 on
#       HIP-34710 and HIP-85088, far less on eta Car and the Statue of Liberty). Confidence: moderate; if it
#       does not hold, the level-plane diagnostic (issue #43's task) reads the ring first.
#   Three seeds pair with pool's three; no difference is claimed inside the threshold.
#
# WHAT IT CANNOT SETTLE. Whether E12's recipe (the best by R) gains the same from the fix; that needs its
#   own control cache re-exported. Unseen sensors, as E13.
# ---------------------------------------------------------------------------------------------------
#
# DEVIATION, 2026-09-27 17:15, after training and before any ring-fixed score: the ring-fixed caches come
#   from 2026-09-25-full for ALL eleven fields, not from re-exports of the same cells. The eval4b tiles came
#   from 2025-2026-organized and the eval4 tiles from 2025-2026-darkscaled, bakes of older code (bilinear
#   warp, older calibration); restacking there with today's code changes more than the stretch, and
#   re-stretching their tiles outside the product would port its stretch into Python. So for those seven
#   fields old against rf is NOT a ring-only contrast; for the four bb-eval4 fields it is. The primary read
#   is unchanged: every model in one pass on identical tiles. Measured first (the ring census, covered
#   floor against the whole-frame one): in 2026-09-25-full six eval sessions' stretch moves (Carina-Wide,
#   exported unstretched until now; HIP-85088; V1045 Ori; eta Car 2026-02-20; Horsehead 2025-10-28; Skull
#   and Crossbones 2026-02-14, which is eval4's "Statue of Liberty" field, OBJECT Skull, 43 subs), so those
#   six are restacked with --rebuild-session and the other five keep their tiles. eval4's fields are named
#   by today's ids in the rf domain (Rim-Nebula/2025-05-02, Horsehead-Nebula/2025-10-28,
#   Skull-and-Crossbones-Nebula/2026-02-14). The predictions stand as written.
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\e14.pid.
#   $s = (Resolve-Path .\run-poolrf.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# It trains only; the two-domain scoring runs once the ring-fixed eval caches exist. To stop it between
# seeds, create C:\temp\e2\e14.stop; NEVER stop the process itself (it owns its child's output pipe).
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int]$SeedCount = 3
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'e14.status'
$log = Join-Path $LogDir 'e14.log'
$stopFile = Join-Path $LogDir 'e14.stop'
$PID | Out-File (Join-Path $LogDir 'e14.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
Set-Status 'starting'

try {
    $cache = Join-Path $Scratch 'n2n-pool-rf'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) { throw "no ring-fixed cache at $cache; run run-pool-reexport.ps1 first" }
    "E14 poolrf at $(git -C $PSScriptRoot rev-parse --short HEAD), cache $cache" | Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-e14'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-poolrf.ps1 $snap -Force

    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        if (Test-Path $stopFile) { "stop file present before seed $seed" | Tee-Object -FilePath $log -Append; break seeds }
        $out = "poolrf_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train poolrf seed $seed : present, skipped" | Tee-Object -FilePath $log -Append; continue }
        Set-Status "train poolrf seed $seed"
        "train poolrf seed $seed -> $out (--base 32 --cond, --schedule plateau) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        # E13's pool arm, flag for flag (run-pool.ps1, step 4).
        & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --base 32 --cond `
            --band-loss 3 --band-scales "2,4 4,8" --schedule plateau --steps 80000 `
            --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
            --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train poolrf failed for seed $seed" }
    }
    "done $(Get-Date -Format o): trained" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
