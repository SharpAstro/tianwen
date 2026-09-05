# Arm X (docs/plans/denoiser-training.md H8 / E8): does a CROSS-NIGHT N2N pair, whose two sides share
# the signal and nothing else, lift the shared-noise ceiling every same-session pair sits under?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the first seed. Written 2026-09-05.
#
# H8 (cross-night pairs give N2N genuinely independent noise). PREDICTION: at matched noise removed on
#   eval4b, arm X spends LESS faint-structure amplitude than the pool-matched control (xctl, same four
#   Rim nights, same-session N2N on real subs, the v19d recipe) and lands at or below the supervised
#   arms' 1.1 to 1.3 on the EXTENDED column of the per-session-cap split, with no loss on stars.
#   Confidence: LOW, and lower than when H8 was written. The residual-correlation statistic could not
#   see a shared-noise component in the same-session halves on this sky at all (run log 2026-09-05,
#   "the paired cache"), so the mechanism the prediction rests on is unmeasured, not confirmed.
#   KILL: arm X is no better than xctl across three seeds. Then the supervised regime's ceiling is not
#   shared noise, the mechanism paragraph of 2026-09-04 is wrong, and the next suspect is the target's
#   own 1/sqrt(N) noise, which a cross-night pair carries exactly as a same-session one does.
#
# What this run CANNOT settle: a win for X over xctl that is also a win for X over the E2 arms could be
#   the pool (six pairs of ONE sky, Rim) rather than the regime; v24 measured the pool draw carrying
#   more variance than most effects chased here, and 360 cells of one nebula is a narrow pool. That is
#   why xctl exists: it is the same pool with only the regime changed, and the H8 verdict is X against
#   xctl. X against warped is reported, not decided on.
#
# Three things fixed by the data, not by choice: the training pool is the six Rim pairs (every
#   combination of four nights), because HD 74167 is the only other object with a pair and one pair
#   cannot train; the gate selects on the HD 74167 pair (another sky, so no night it trained on); and
#   eval4's Rim observer is the SAME NIGHT as pair member 2025-05-02, so X is scored on eval4b and on
#   eval4's Horsehead and Statue of Liberty cells only, never on eval4's Rim.
#
# Both sides of a pair were resampled onto the midpoint grid, 1.85 to 2.15 px, so X trains on a PSF
#   16 percent softer than the deployed master's. Recorded here so a star-amplitude result is read
#   with that in mind; xctl trains on the bake's tiles at the native PSF.
# ---------------------------------------------------------------------------------------------------
#
# Caches (both prepared 2026-09-05, list files under arms\):
#   n2n-x      from C:\temp\tianwen-scratch\n2n-pairs (tianwen dataset pair): x-train-6 / x-val-1,
#              60 cells per pair, 120 val; sub slots EMPTY, so the regime is --half-only.
#   n2n-x-ctl  from D:\Astro-Dataset\2026-09-full: xctl-train-4 / xctl-val-2, 90 cells per session
#              (360 train cells, matching X), 120 val; the v19d recipe with --mix-avg.
# Same recipe otherwise, byte for byte the E2 one: l2, upsample, cond, band loss 3 at "2,4 4,8", base
# 32, 4000 steps, gate every 100.
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-x.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string[]]$Arms = @('x', 'xctl'),
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2)
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 3,4,5' arrives as the single string '3,4,5' and coerces to 345; use -Command instead."
    }
}
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'x.status'
$log = Join-Path $LogDir 'x.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8

# Snapshot the scripts this run used, so a result can be re-read against the code that produced it.
$snap = Join-Path $LogDir 'scripts-x'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-x.ps1 $snap -Force
Copy-Item arms\x-*.txt, arms\xctl-*.txt $snap -Force

try {
    foreach ($arm in $Arms) {
        # Named explicitly: "n2n-$arm" read n2n-xctl on the first launch, the cache is n2n-x-ctl, and
        # the skip below fired as designed, so X trained alone and the control arm had to be re-run.
        $cache = Join-Path $Scratch $(if ($arm -eq 'x') { 'n2n-x' } else { 'n2n-x-ctl' })
        # Checked per arm and right before its first seed, not up front: the control cache prepares
        # from the bake while X already trains, and a missing meta.json then means "not yet", so it is
        # skipped and the script re-run, never treated as fatal.
        if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
            "skip $arm : $cache has no meta.json yet (prepare it, then re-run)" | Tee-Object -FilePath $log -Append
            continue
        }
        # The ONLY difference between the arms: the regime. Same recipe, matched cell count, val on
        # the same sky (HD 74167).
        $regime = if ($arm -eq 'x') { '--half-only' } else { '--mix-avg' }
        foreach ($seed in $Seeds) {
            $out = "${arm}_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train $arm seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            "train $arm seed $seed -> $out ($regime)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache $regime --loss l2 --upsample --cond `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train failed for $arm seed $seed" }
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
