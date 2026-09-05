# Arm X2 (docs/plans/denoiser-training.md H8 / E8): the cross-night question asked on ONE axis.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the first seed. Written 2026-09-05, after arm X was killed and its four
# preconditions cleared.
#
# WHY ARM X COULD NOT ANSWER H8. It differed from every supervised arm in two things at once: the
#   target (another night instead of the input's own master) and the input distribution (a night's
#   master at master depth instead of a master plus injected noise across a level range). It never
#   learned the task at all -- 0.88 to 0.92x noise, faint amplitude falling one for one -- so its kill
#   said nothing about targets.
#
# X2 HOLDS THE INPUT FIXED. Both arms train on the SAME cache, the SAME cells and the SAME injected
#   draws of night A. The only difference is which frame the loss regresses onto:
#     x2     --synthetic-target half-b : night B. Its noise is independent of the input's by
#                                        construction, which is the whole of H8.
#     x2ctl  --synthetic-target half-a : night A, the night the input was degraded from, so input and
#                                        target share that night's noise -- the supervised regime.
#   One axis, one comparison. A kill here DOES license the inference arm X's could not.
#
# THE FOUR PRECONDITIONS, cleared:
#   1. PSF matched on the EXPORTED tiles, not the masters (`--psf-tolerance`, iterated: measure both
#      exported sides, convolve the sharper, measure again). Arm X shipped its pairs 3 to 6 percent
#      apart, which teaches a model to blur toward the wider night. Note the kernel is AREA sampled:
#      point sampling a sub-pixel Gaussian is a silent no-op, which this loop found on its first run.
#   2. The pool is more than one sky. Vela SNR and HD 71272 were refused for want of a quad fit, which
#      was a starved matcher: partial overlap, and the brightest 100 stars a side hold no common quad.
#      VELA SNR joins at 400 stars a side (19.2 percent overlap, rms 0.28 px), so the pool is two skies
#      and seven pairs. HD 71272 does NOT: it refuses at 400 and again at the 500-star detection cap,
#      where the matcher is exhausted rather than starved, and the fix is the coordinate seed the plan
#      named -- which needs a plate solve, the retained masters carrying no WCS. Recorded before the
#      seeds ran; the pool file is arms\x2-train-7.txt and says the same.
#   3. A conditioning range that covers deployment. Injected draws span 0.5 to 3 times each night's own
#      measured noise, so the plane is no longer pinned at one sky's master depth (arm X covered 0.22
#      to 0.54 of a deployed 0.09 to 0.83); Vela SNR's own master at 0.08 to 0.15 brings the low end.
#   4. The three-frame measurement runs BEFORE the seeds, on the Rim triple, and is reported whatever
#      it says: cov(A-B, A-C) is night A's own deviation, and its structured share is the part of what
#      a cross-night target hands the model that is not photon noise.
#
# PREDICTION: at matched noise removed on eval4b, x2 spends LESS faint-structure amplitude than x2ctl,
#   on the same cells, across three seeds. Confidence: LOW. The three-frame measurement is what should
#   move it, and it runs first: a structured share above about a third says a cross-night target pays
#   more in scene disagreement than it can win in independence, and I expect the null.
#
# MEASURED FIRST, 2026-09-05 23:46, before any seed (120 cells of the Rim triple, C:\temp\e2\threeframe.json):
#   the structured share is 5.5 to 18.6 percent of each night's own deviation IN VARIANCE (0.0120 /
#   0.0127 / 0.0206 sigma total, of which 0.0052 / 0.0040 / 0.0048 is structured). That is well under
#   the third I named, so the pre-registered threshold says the tax is small and the mechanism has
#   room: CONFIDENCE RAISED from LOW to MEDIUM, with the prediction and the kill unchanged. Two
#   caveats travel with the number: a component shared by all three nights (one master dark among
#   them) cancels in every difference and is invisible here, and a fixed-pattern residual is
#   pixel-scale, so 5.5 to 18.6 percent is a LOWER bound on the non-photon part.
# KILL: x2 is no better than x2ctl on the amplitude columns at matched removal across three seeds.
#   Then a shared-noise target is NOT what limits the supervised regime, H8 is refuted rather than
#   parked, and the next suspect is the target's own 1/sqrt(N) noise, which both arms carry alike.
# WHAT THIS STILL CANNOT SETTLE: the pairs' PSF is the midpoint grid's, about 16 percent softer than a
#   deployed master's, and that is common to both arms. A star-amplitude number from either is read
#   against that, never against the E2 arms directly.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-x2.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-full',
    [string]$Export = 'D:\Astro-Dataset\pairs-x2',
    [string]$Triple = 'D:\Astro-Dataset\pairs-triple',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int[]]$Seeds = @(0, 1, 2),
    [switch]$ExportOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 3,4,5' arrives as the single string '3,4,5' and coerces to 345; use -Command instead."
    }
}
if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'x2.status'
$log = Join-Path $LogDir 'x2.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8

$snap = Join-Path $LogDir 'scripts-x2'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-x2.ps1 $snap -Force
Copy-Item arms\x2-*.txt $snap -Force

function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}

# Session ids of the bake, by object and night. Written out rather than derived, so a pair that fails
# to export is a visible refusal instead of a silently smaller pool.
$camFilter = 'ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm'
$cam = 'ZWO ASI533MC Pro'
$filt = 'Optolong L-Ultimate 3nm'
function Sid([string]$folder, [string]$night, [string]$obj) { "$camFilter/$folder/$night|$cam|$obj|$filt" }
$rim = @('2025-05-02', '2026-02-16', '2026-02-18', '2026-02-20') | ForEach-Object { Sid 'Rim-Nebula' $_ 'Rim Nebula' }
$trainPairs = @()
for ($i = 0; $i -lt $rim.Count; $i++) {
    for ($j = $i + 1; $j -lt $rim.Count; $j++) { $trainPairs += "$($rim[$i])::$($rim[$j])" }
}
$trainPairs += (Sid 'Vela-SNR' '2025-12-17' 'Vela SNR') + '::' + (Sid 'Vela-SNR' '2026-01-23' 'Vela SNR')
$trainPairs += (Sid 'HD-258924' '2026-01-20' 'HD 71272') + '::' + (Sid 'Vela-SNR' '2026-01-23' 'HD 71272')
$valPair = (Sid 'HD-74167' '2026-01-05' 'HD 74167') + '::' + (Sid 'HD-74167' '2026-01-06' 'HD 74167')
# The measurement triple: three Rim nights on one grid. Its own directory, because a pair id carries
# only its first two nights and a triple would otherwise collide with the pair of the same two.
$triplePair = (Sid 'Rim-Nebula' '2026-02-16' 'Rim Nebula') + '::' + (Sid 'Rim-Nebula' '2026-02-18' 'Rim Nebula') + '::' + (Sid 'Rim-Nebula' '2026-02-20' 'Rim Nebula')

try {
    # 1. The pair cache: eight training pairs at 45 cells, the held-out pair at 120, eight injected
    #    draws of night A per cell. --psf-match convolves where the nights differ (Vela SNR 6 percent,
    #    HD 71272 15); the exported-side loop then closes what the warp reopens.
    $exportStatus = Join-Path $Export 'pairs.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -eq 'done') {
        "export : $Export already done, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $pairArgs = @()
        foreach ($p in $trainPairs) { $pairArgs += '--pair'; $pairArgs += $p }
        "export -> $Export ($($trainPairs.Count) training pairs, 45 cells, 8 injected draws)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset pair --bake $Bake --out $Export --cells 45 --seed 1 --psf-match `
            --inject-draws 8 --warp-sigma 0.5 @pairArgs *>> (Join-Path $Export 'pair.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "pair export failed (exit $LASTEXITCODE)" }
        "export -> $Export (the held-out pair, 120 cells)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset pair --bake $Bake --out $Export --cells 120 --seed 1 --psf-match `
            --inject-draws 8 --warp-sigma 0.5 --pair $valPair *>> (Join-Path $Export 'pair.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "val pair export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $exportStatus -Encoding utf8
    }
    Get-Content (Join-Path $Export 'pair.log') | Select-String '\[pair\]|skipped:' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append

    # Every pre-registered pair must be in the manifest: a pool that quietly shrank is the one thing
    # this arm cannot afford, having been killed once for training on too little sky.
    $exported = (Get-Content (Join-Path $Export 'pairs.jsonl') | ForEach-Object { ($_ | ConvertFrom-Json).PairId })
    foreach ($want in (@(Read-List 'arms\x2-train-7.txt') + @(Read-List 'arms\x2-val-1.txt'))) {
        if ($exported -notcontains $want) { throw "pre-registered pair missing from the export: $want" }
    }

    # 2. The three-frame measurement, before any seed and reported whatever it says.
    $tripleStatus = Join-Path $Triple 'pairs.status'
    if ((Test-Path $tripleStatus) -and ((Get-Content $tripleStatus -Raw).Trim() -eq 'done')) {
        "triple : $Triple already done, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        New-Item -ItemType Directory -Force $Triple | Out-Null
        "running $(Get-Date -Format o)" | Out-File $tripleStatus -Encoding utf8
        "export -> $Triple (the Rim triple, 120 cells, no injection)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset pair --bake $Bake --out $Triple --cells 120 --seed 1 --psf-match --pair $triplePair `
            *>> (Join-Path $Triple 'pair.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $tripleStatus -Encoding utf8; throw "triple export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $tripleStatus -Encoding utf8
    }
    "three-frame measurement:" | Tee-Object -FilePath $log -Append
    & python n2n_threeframe.py --root $Triple --json (Join-Path $LogDir 'threeframe.json') *>> $log
    if ($LASTEXITCODE -ne 0) { throw "the three-frame measurement failed" }
    if ($ExportOnly) { "done (export only) $(Get-Date -Format o)" | Out-File $status -Encoding utf8; return }

    # 3. Prepare, with run-e2.ps1's staleness gate.
    $cache = Join-Path $Scratch 'n2n-x2'
    $meta = Join-Path $cache 'meta.json'
    if (Test-Path $meta) {
        $rows = Join-Path $Export 'pairs.jsonl'
        if ((Get-Item $rows).LastWriteTimeUtc -gt (Get-Item $meta).LastWriteTimeUtc) {
            throw "cache $cache is older than the export it came from ($rows). Delete the cache and re-run; a prepared cache is never edited in place."
        }
        "prepare : cache already present and newer than the export, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        "prepare -> $cache" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\x2-train-7.txt --val-from-list arms\x2-val-1.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed" }
    }

    # 4. Train: the E2 recipe byte for byte, three seeds each, the two arms differing ONLY in the target.
    foreach ($arm in @('x2', 'x2ctl')) {
        $target = if ($arm -eq 'x2') { 'half-b' } else { 'half-a' }
        foreach ($seed in $Seeds) {
            $out = "${arm}_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train $arm seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            "train $arm seed $seed -> $out (--synthetic --synthetic-target $target)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --synthetic-target $target --loss l2 --upsample --cond `
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
