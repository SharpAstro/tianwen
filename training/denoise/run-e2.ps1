# E2 (docs/plans/denoiser-training.md): does SUPERVISED synthetic injection beat N2N at the depth the
# denoiser is deployed at, and does the injected noise SHAPE matter?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the prepare. Written 2026-09-03.
#
# H1 (supervised injection beats N2N at deployment depth). PREDICTION: on half-master inputs scored
#   against the OTHER half, both synthetic arms beat the v19d controls on faint amplitude at matched
#   noise. Confidence: moderate. The mechanism is that v19d only ever saw pairs at 2.96x the master's
#   noise and has to extrapolate down to 1.0x, while an injected arm is trained across [0.1, 1.5] of a
#   sub with the deployment depth interior to the range.
#   KILL: the arms do not separate from the controls across three seeds. Then the master-depth gap is
#   not a training-distribution problem and the next suspect is the architecture.
#
# H2 (shape has to be injected, not only level). PREDICTION: S-warped beats S-white by at least 0.02
#   faint amplitude at matched noise, and its residual band1/band0 on real inputs sits closer to the
#   0.463 a real half-master reads. Confidence: LOW, and lower than when the hypothesis was written.
#   E1 measured what the two arms actually are: white injects at band1/band0 0.216 and warped at 0.460
#   against that 0.463 target, so the arms differ in shape by a factor of two and the question is
#   whether the net cares. It might not: the conditioning plane reads a scalar level, not a shape.
#   KILL: the two arms overlap across three seeds. Then shape is not the lever, S-white is the cheaper
#   recipe, and --warp-sigma becomes a knob nobody has to set.
#
# ADDENDUM 2026-09-03, after launch, correcting the REASONING above and not the prediction or the
#   kill criterion (those stand as written; a pre-registration that gets edited to fit is not one).
#   H2's low confidence rested on "the conditioning plane reads a scalar level, not a shape", which is
#   only half the mechanism. That half is right: the net cannot be TOLD the shape. But the other half
#   does not need telling. A denoiser trained on white noise LEARNS a filter tuned to white noise, and
#   applied to correlated noise it mis-smooths regardless of what it is handed as conditioning. Both
#   arms are scored on the same REAL inputs, which carry the real correlated shape, so E2 does test
#   that mechanism. Confidence stays low for a different reason: nobody has measured how much a
#   learned filter's shape-specificity is worth at these depths.
#
#   What E2 CANNOT settle is an overlap. "Shape does not matter" and "this architecture cannot express
#   shape" look identical then, and the existing band-conditioning null (v14, v21) does not separate
#   them either: every pair those runs ever saw had the same shape (0.59 to 0.60), so the plane had no
#   shape variation to learn from. That null is about the data, not the mechanism.
#
#   E2b, defined NOW so it cannot be invented to fit the result: an arm that draws --warp-sigma PER
#   DRAW the way depth is already drawn, so one training set spans the sub-to-master shape range. It is
#   the first data on which --cond-bands is testable at all, and it is the recipe that would ship,
#   since a user's frame can sit anywhere on that range. TRIGGER: run E2b if the arms OVERLAP or if
#   warped wins; skip it only if white wins outright. The overlap is the interesting case, not the
#   dull one, which is why the trigger is not "if H2 lands".
#
# NOT predicted, and worth watching: whether the level prior (H3) is weaker in the synthetic arms.
#   They see the whole [0.1, 1.5] range on every session rather than eight sessions' worth of sky
#   levels, so if the prior is a training-distribution artefact this is where it should loosen.
# ---------------------------------------------------------------------------------------------------
#
# Depth variety comes from the INJECTION here, not from --mix-avg: every draw carries its own
# log-uniform depth over [0.1, 1.5] of one sub, so one arm already spans the levels the mixed-average
# regime was built to give an N2N pair. --mix-avg is therefore absent rather than forgotten; with
# --synthetic the regime list is exactly one entry and mix-avg would be silently ignored anyway.
#
# The two arms share --seed 1 in the EXPORT, so they hold the same cells and the same drawn depths and
# differ in exactly one thing: the shape of the injected noise field. Do not re-export one arm alone.
#
# SESSIONS ARE PINNED BY LIST, AND SIX ARE EXCLUDED. The eval4 observers this arm is scored on are
# darkscaled bakes of nights that ALSO sit in the organized pool under different ids: Skull and
# Crossbones 2026-02-14 and Rim Nebula 2025-05-02 are the same night, and four more Rim/Horsehead
# sessions are the same rig on the same target another night, which for a denoiser judged on faint
# structure is the same leak. arms/e2-train-8.txt and arms/e2-val-2.txt are drawn from the 45 eligible
# sessions by the trainer's own rule (sorted, Random(42).shuffle), so the split is stated rather than
# incidental and an arm is never scored on a scene it trained on.
#
# THE MATCHED CONTROL (added 2026-09-03, after the white arm's first scoring). The v19d controls were
# trained on the DARKSCALED pool and these arms on ORGANIZED, so a difference between them confounds
# the regime under test with the pool, and v24 already measured the pool draw carrying more variance
# than most effects chased here. The 'control' arm is therefore an N2N run on the SAME pool, the SAME
# eight sessions and the SAME cells as the synthetic arms, with only the regime different: it prepares
# from the BAKE (real subs) instead of a degraded export, and trains the v19d recipe with --mix-avg and
# without --synthetic. Without it E2 cannot say whether supervised injection or the pool moved a number.
#
# Run detached; read the status file, never the log. Use -Command and NOT -File whenever an array
# parameter is passed: under -File every argument is a bare string, so `-Seeds 3 4 5 6 7 8` binds only
# the 3 and lets 4 fall through POSITIONALLY onto -Exports (the run then hunts for the export under a
# directory named "4"), while `-Seeds 3,4,5,6,7,8` binds the whole thing as the single string
# "3,4,5,6,7,8" and trains one run it calls seed 345678. Both happened, in that order, on 2026-09-03.
#   $s = "$PWD\run-e2.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s' -Arms warped -Seeds 3,4,5,6" -WindowStyle Hidden
param(
    [string[]]$Arms = @('white', 'warped'),
    [string]$Exports = 'D:\Astro-Dataset\degraded',
    [string]$Bake = 'D:/Astro-Dataset/2025-2026-organized',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2),
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A mis-bound argument must not present itself as a missing export. Both of the -File binding traps
# above land here, as $Exports = '4' or a $Seeds of one absurd number, and the first one's only symptom
# was "export says 'missing'" -- which reads as "the export is not finished yet", the one message that
# invites you to wait rather than to look.
if (-not (Test-Path -PathType Container $Exports)) {
    throw "Exports root '$Exports' is not a directory. If you passed -Seeds under pwsh -File, the extra values bound positionally onto -Exports; use -Command instead."
}
foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 3,4,5' arrives as the single string '3,4,5' and coerces to 345; use -Command instead."
    }
}
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'e2.status'
$log = Join-Path $LogDir 'e2.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8

# Snapshot the scripts this run used, so a result can be re-read against the code that produced it.
$snap = Join-Path $LogDir 'scripts'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-e2.ps1 $snap -Force

try {
    # An arm whose export is still running is SKIPPED, not fatal: the two arms are independent runs
    # that meet only at scoring, so the first can train while the second exports. Re-run this script
    # for the rest; a prepared cache and an existing checkpoint are both skipped.
    $ready = @()
    foreach ($arm in $Arms) {
        if ($arm -eq 'control') { $ready += $arm; continue }   # reads the bake, not an export
        $armStatus = Join-Path (Join-Path $Exports $arm) 'degrade.status'
        $state = if (Test-Path $armStatus) { (Get-Content $armStatus -Raw).Trim() } else { 'missing' }
        if ($state -eq 'done') { $ready += $arm }
        else { "skip $arm : export says '$state'" | Tee-Object -FilePath $log -Append }
    }
    if ($ready.Count -eq 0) { throw "no export arm is done (looked for: $($Arms -join ', '))" }

    foreach ($arm in $ready) {
        $root = if ($arm -eq 'control') { $Bake } else { Join-Path $Exports $arm }
        $cache = Join-Path $Scratch "n2n-e2-$arm"
        $meta = Join-Path $cache 'meta.json'
        if (Test-Path $meta) {
            # STALENESS GATE. "The cache exists" is not "the cache is of THIS export": a re-export
            # leaves the old cache in place and the skip below would then train on the tiles that
            # were re-exported to get rid of. It happened once (the depth-range fix), and it is
            # invisible, because the run reports "skipped" and trains happily on the wrong bytes.
            # Refusing rather than deleting: 2.5 GB is the user's to throw away, not this script's.
            $rows = if ($arm -eq 'control') { Join-Path $Bake 'tiles-manifest.jsonl' } else { Join-Path (Join-Path $Exports $arm) 'degradations.jsonl' }
            if ((Get-Item $rows).LastWriteTimeUtc -gt (Get-Item $meta).LastWriteTimeUtc) {
                throw "cache $cache is older than the export it came from ($rows). Delete the cache and re-run; a prepared cache is never edited in place."
            }
            "prepare $arm : cache already present and newer than the export, skipped" | Tee-Object -FilePath $log -Append
        }
        else {
            "prepare $arm -> $cache" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --prepare --root $root --cache $cache `
                --train-from-list arms\e2-train-8.txt --val-from-list arms\e2-val-2.txt `
                --cells-per-session 45 --val-cells-per-session 120 *>> $log
            if ($LASTEXITCODE -ne 0) { throw "prepare failed for $arm" }
        }
    }

    if (-not $PrepareOnly) {
        foreach ($arm in $ready) {
            $cache = Join-Path $Scratch "n2n-e2-$arm"
            foreach ($seed in $Seeds) {
                $out = "e2_${arm}_s$seed.pt"
                if (Test-Path (Join-Path $cache $out)) {
                    "train $arm seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                    continue
                }
                "train $arm seed $seed -> $out" | Tee-Object -FilePath $log -Append
                # The ONLY difference between the control and the synthetic arms: the regime. Same
                # recipe, same pool, same sessions, same cells.
                $regime = if ($arm -eq 'control') { '--mix-avg' } else { '--synthetic' }
                & python n2n_smoke.py --train --cache $cache $regime --loss l2 --upsample --cond `
                    --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 `
                    --seed $seed --out $out *>> $log
                if ($LASTEXITCODE -ne 0) { throw "train failed for $arm seed $seed" }
            }
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
