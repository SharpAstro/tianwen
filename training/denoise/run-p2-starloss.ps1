# P2 E2.8 (H10): the star-term loss. Does an objective that counts STARS instead of pixels make the
# width minimum gate-eligible?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-07 from the plan's "The next four" (2026-09-06).
#
# WHY. E2.7 showed the loss can see the scale once the fine band is supervised, and that it buys width
#   with faint stars: the best MINIMUM was 1.328 out/truth holding 0.54 of the truth's stars against
#   a 0.62 floor, so no minimum was ever selectable. That is arithmetic, not a subtlety: any pixel-wise
#   L2 weights error by amplitude squared times pixel count, a faint star is ten pixels at a few
#   sigma, about 1e-4 of a tile's loss, and suppressing it is free.
#
# WHAT IT IS. E2.7 arm B byte-for-byte (--band-scales "1,2", base 32, 4000 steps, gate every 100, the
#   same 78-session cache, NOT re-exported) plus --star-loss auto: per detected star (the gate's own
#   detector on the clean target, <= 32 a tile, evenly spaced in peak rank) a 7x7 window, and the sum
#   of |log flux ratio (r<=3)| + |log peak ratio| + |log concentration ratio (E(r<=1.5)/E(r<=3))|,
#   equal weight over stars. The weight is set ONCE so the term equals the pixel term on the first
#   starred batch, then FIXED, logged and saved in the checkpoint; it is never tuned on the gate.
#   Seeds 0..4. E2.7 arm B seeds 0..2 (p2band_b_s*.pt, p2-band.log) are the paired control.
#
# A PROPERTY OF THE RECIPE, seen on the 30-step smoke and stated here rather than discovered later: on
#   step 1 the pixel term is dominated by the input's injected sub-level noise (1.02e-2), so the
#   matched weight (2.29e-3) leaves the star term about 30x the pixel term at E2.6's plateau of 8e-5.
#   Late in training the star term IS the objective and the pixel and band terms regularise it. That
#   is what "equals L2 on the first batch" means as written, and the run keeps it; if the arm fails by
#   fabrication (stars over 1.10, or stars@6 falling while stars holds) the weight is the first suspect.
#
# PREDICTION: the gate selects on >= 4 of 5 seeds; the selected out/truth <= 1.30; at the width minimum
#   the stars column reads >= 0.60 against E2.7's 0.54 to 0.60, so the minimum itself becomes
#   gate-eligible. PRIMARY readout is the stars column at the minimum (a 0.06 effect against a small
#   spread); the width effect (1.328 -> 1.30) sits at the edge of what five seeds resolve (paired sd
#   0.008 to 0.031, five seeds resolve ~0.04). Confidence MODERATE: the term rewards what the gate
#   demands, but a 7x7 window and a concentration proxy are blunt.
# ALSO WATCH stars@6 (new gate column, report-only): a net can sharpen the 12-MAD stars the term sees
#   while suppressing the population under STAR_SIGMA; the low-bar count is the check for that.
# KILL LINE: width minima still hold under 0.60 stars in >= 3 of 5 seeds, or the selected width is
#   >= 1.36 (no gain on arm B seed 2's 1.365). Then pixel-domain losses are exhausted for this net and
#   the fork is: unrolled RL if E1b passed, capacity (NAFNet-32) if it did not, both carrying the star
#   term regardless.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-p2-starloss.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2, 3, 4)
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 0,1,2' arrives as the single string '0,1,2' and coerces to 12; use -Command instead."
    }
}

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'p2-star.status'
$log = Join-Path $LogDir 'p2-star.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-p2-star'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-p2-starloss.ps1 $snap -Force

# The SAME cache E2.6 and E2.7 trained on. stars.npy is ADDED to it by --prepare-stars, which reads
# slot 0 and writes nothing else; the tiles E2.7's control trained on are untouched.
$cache = Join-Path $Scratch 'n2n-p2-blur'
if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
    throw "no cache at $cache. E2.6 built it; this arm deliberately does not rebuild it, because a fresh export would break the paired comparison against E2.7 arm B."
}

try {
    if (-not (Test-Path (Join-Path $cache 'stars.npy'))) {
        "prepare-stars on $cache" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare-stars --cache $cache *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare-stars failed" }
    }

    "=== arm star: E2.7 arm B + --star-loss auto" | Tee-Object -FilePath $log -Append
    foreach ($seed in $Seeds) {
        $out = "p2star_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) {
            "train seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        "train seed $seed -> $out" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --cond-psf01 --loss l2 --upsample `
            --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
            --star-loss auto --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for seed $seed" }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
