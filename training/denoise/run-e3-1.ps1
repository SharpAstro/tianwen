# P2 E3.1: the unrolled Richardson-Lucy operator WITH the learned residual prior, five seeds.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's E3.0 reading
# (docs/plans/deconvolver-training.md, "E3.0, read 2026-09-13" and "What is pre-registered before a
# seed is trained").
#
# WHY. E3.0, the operator alone (K = 20, the row's estimated kernel, no network), recovers the gate
#   session from 1.376 to 1.122 keeping 0.98 of the truth's stars, and manufactures 20.5 times the
#   truth's detections on the observer session (12.1x its input null of 1.69). The noise-free control
#   fabricates nothing on either session with either kernel, so the kill is Richardson-Lucy's own
#   amplification of the injected noise, which is the job the plan gave the prior: "the network
#   carries what the oracle loses, the stars and the ringing under noise".
#
# WHAT IT IS. n2n_operator.RLOperator(K = 20) with a StretchedPrior: a base-16 residual U-Net applied
#   to the estimate after EVERY iteration in the stretched domain, output convolution zero-initialised
#   so step 0 of every seed IS E3.0's row. Objective = E2.7 arm B's pixel (L2) and band (3 x "1,2")
#   terms plus the star term WITH its empty counterpart at E2.8c's fixed weight 1.84e-3. Same cache,
#   gate (HIP 80609) and observer (SV605CC Pleiades / Triangulum) as E3.0; 4000 steps, batch 8, the
#   gate every 100 steps; seeds 0 to 4 in sequence (the GPU is never shared). The step is 2.95 s on
#   the 1070 (the prior's backward under recompute; the grouped convolution is 0.24 s of it), so a
#   seed is about 3.3 h and the five about 16.5 h.
#
# PREDICTION. At least one seed of five selects at a gate width at or under 1.15 holding stars at or
#   over 0.85, with the observer's stars under 2x its null (3.38) at that step. Confidence MODERATE: the
#   prior starts at the identity and the loss sees the fabrication only through the empty-window term
#   and the pixel term on the observer-like cells of the train sessions; the observer session's noise
#   (1.41x its master) is at the noisy end of the training pool.
# PRIMARY READOUT: n2n_gatelog.py C:/temp/e2/e31.log, the selected out/truth, stars and the observer
#   columns side by side, read against E3.0's row (1.122 / 0.98 / 20.5).
# KILL LINE (two, from the plan): no seed improves on the observer's 20.5 at a gate width at or under
#   E3.0's 1.122 (the prior is not earning its place; the operator ships physics-only with the
#   estimator step); or a seed reaches the observer bound only by widening the gate past 1.20 (the
#   prior traded the sharpening for smoothing: E2.8b's arm N again, not a deconvolver).
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log while it runs:
#   $s = "$PWD\run-e3-1.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2, 3, 4),
    [int]$K = 20,
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
$status = Join-Path $LogDir 'e31.status'
$log = Join-Path $LogDir 'e31.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-e31'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e3-1.ps1 $snap -Force

$cache = Join-Path $Scratch 'n2n-p2-blur-clamped'
foreach ($f in 'meta.json', 'kernels.npy', 'stretch.npy', 'stars.npy', 'empty.npy') {
    if (-not (Test-Path (Join-Path $cache $f))) {
        throw "no $f at $cache. run-e3-prepare.ps1 built it; this arm deliberately does not rebuild it."
    }
}

try {
    "=== E3.1: RL K=$K with a zero-initialised residual prior every iteration, star term $Weight with its empty counterpart" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "e31_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) {
            "train E3.1 seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        "train E3.1 seed $seed -> $out $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python -u n2n_smoke.py --train --cache $cache --operator rl --rl-k $K --cond-psf01 --synthetic `
            --loss l2 --upsample --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss $Weight --star-loss-empty --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for E3.1 seed $seed" }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
