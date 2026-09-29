# E16b (docs/plans/denoiser-training.md, run log, "E16b, pre-registered" and its amendments): E16a's recipe with the
# noise injected per channel on each master's own recorded calibration, shaped by the master's integration (the control,
# convmap3), and the same plus bright cells (the arm, convmapb). The plan's entry is the record; this header is its copy.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED 2026-09-29, before the recipe-3 store finished baking and before any cell of it was looked at;
# AMENDED three times (the plan's amendments have the reasons): the same evening, before any export, cache or model
# existed; on 2026-09-30, after the first export failed on the sessions the bake never halved, before any cache or model;
# and on 2026-09-30 again, after D2 failed the sub anchor (a sub is on its own unit scale, and a drizzle's colours are not
# sqrt(N) deep), with caches but no model: the owner chose the master anchor and D2's bright half amended as below.
#
# ARMS. convmap3: bb-ctl-14 / bb-val-2, degrade --cells 120 --seed 1, prepare 45 per session, --cond-map, run-e16a's
#   training line, seeds 0..3, the noise injected per channel from each master's own recorded calibration (tianwen
#   dataset degrade --noise-anchor master-calibration), shaped as the master's integration shapes its noise (--warp-sigma
#   0.5 for a demosaiced master, --warp-sigma-drizzle 0 for a Bayer-drizzled one: half pairs 0.45 and 0.31-0.33 band1/band0).
#   convmapb: the same plus bright cells (tianwen dataset bright-cells: at least 10 percent of
#   a cell's level in [0.45, 0.95), the scorer's low-pass; up to 45 per session). The fourteen sessions hold 4 bright
#   cells in one session, under the rule's 150 over three, so the pool widens to arms\e16b-widen.txt (the store's other
#   sessions ranked by bright count, test sessions and eval-field plates out), their bright cells only (--listed-only).
#   It ran out at 41 more, 45 in all; the owner chose (2026-09-30) to run the arm on them, labelled a third of the
#   registered dose: -MinArmCells 45.
#   The seeds are interleaved (control 0, arm 0, ...). Both caches are cut from ONE export: the control leaves the
#   listed cells out of the pick (--exclude-cells), the arm leaves them out and adds them after (--extra-cells).
#
# EVAL. The eleven fields' per-channel caches (-rfpc cells, from 2026-09-28-evalplanes-pc) plus up to 20 bright cells
#   each (level read off half B, as the scorer bins it), as -rfpcb. Conditions orc (--plane-truth-anchor, primary) and
#   est (each tile's own plane). Scored: convmap3_s0..3, convmapb_s0..3, convmap_s0..3 (E16a), convrf_s0..3, shipped.
#
# CHECKS, before any training; a failure stops the run.
#   D1 (amended) the control's keys equal n2n-bb-ctl-rfm's outside the Pleiades and Triangulum (whose P0 samples the
#      recipe-3 store moved by 2 of 300 cells), and its clean (slot 0) tiles are E16a's within 2 fp16 steps.
#   D2 (amended) tianwen dataset noise-check --anchor master-calibration --bright-gate relative: against the half pairs
#      of every session the bake halved, the master anchor's measured-over-predicted within 10 percent on quiet cells,
#      per channel; and in every session with both, its bright cells' within 15 percent of its quiet cells' (the level
#      model apart from the session's anchor). The sub-calibration, half-pair and sub-MAD anchors printed beside it.
#   D3 the bright eval cells make the level at 0.60 and up readable on at least four fields and 0.45-0.60 on six.
#
# PREDICTIONS (orc, full strength, convmapb against convmap3, fields readable for both; bands 0-1 / 1-2 / 2-4 px):
#   (1) 0.45-0.60: the 0-1 px error left falls by at least 0.10. Moderate.
#   (2) 0.60 and up: the same fall. Low to moderate.
#   (3) bright detail kept (both levels, 1-2 px) at least 0.97. KILL below 0.92.
#   (4) error left within 0.05 of the control's in every band below 0.45; R within 4 points.
#   (5) the frontier no worse than the control's on all but two readable fields.
#   (6) est against orc for convmapb: full-strength removal within 4 points on the eleven fields' mean, the largest
#       excess on Lagoon.
#   KILL for the arm: (1) falls under 0.03, or (3)'s kill.
#   The control against convmap: within 0.05 of error left everywhere and 4 points of R. Low confidence.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\e16b.pid.
#   $s = (Resolve-Path .\run-e16b.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between stages or seeds, create C:\temp\e2\e16b.stop; NEVER stop the process itself. Every stage is
# skipped when its output exists, so a re-launch resumes.
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-29-full',
    [string]$EvalBake = 'D:/Astro-Dataset/2026-09-28-evalplanes-pc',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'degraded\e16b'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe",
    [int]$SeedCount = 4,
    [string]$Tag = 'e16b',
    # 'control' runs everything but the arm (its cache, seeds and scores), which waits on the owner's decision about
    # the bright pool: the widening fell short of the pre-registered 150 cells (the plan's amendment). 'both' is the
    # design as registered; a re-launch with it resumes, every finished stage skipped.
    [ValidateSet('both', 'control')]
    [string]$Arms = 'both',
    # The fewest bright cells the arm runs on. 150 is the design as registered; the owner chose on 2026-09-30 to run the
    # arm on the 45 the store holds (a third of the dose, labelled so), which is -MinArmCells 45.
    [int]$MinArmCells = 150
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir "$Tag.status"
$log = Join-Path $LogDir "$Tag.log"
$stopFile = Join-Path $LogDir "$Tag.stop"
$lists = Join-Path $LogDir "$Tag-lists"
$PID | Out-File (Join-Path $LogDir "$Tag.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
function Session-Args([string[]]$ids) { foreach ($id in $ids) { '--session'; $id } }
function Stop-Requested([string]$before) {
    if (Test-Path $stopFile) {
        "stop file present before $before; ending here" | Tee-Object -FilePath $log -Append
        "stopped $(Get-Date -Format o): before $before" | Out-File $status -Encoding utf8
        return $true
    }
    return $false
}
function Final([string]$dir, [string]$name) {
    $f = Join-Path $dir "${name}_final.pt"
    if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
}
function Invoke-Tianwen([string]$what, [string[]]$arguments, [string]$out) {
    & $Tianwen @arguments *>> $out
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE); see $out" }
}
Set-Status 'starting'

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    $anchor = git -C $PSScriptRoot log -1 --format=%cI --grep "the injected noise is anchored on each master's own calibration"
    if (-not $anchor) { throw 'the master-calibration anchor is not in this checkout' }
    $head = git -C $PSScriptRoot log -1 --format=%cI
    $built = (Get-Item $Tianwen).LastWriteTime
    foreach ($t in @($anchor, $head)) {
        if ($built -lt [datetimeoffset]::Parse($t).LocalDateTime) { throw "the CLI was built $($built.ToString('o')), before $t; rebuild TianWen.Cli in Release" }
    }
    "E16b at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $($built.ToString('o'))" | Tee-Object -FilePath $log -Append
    New-Item -ItemType Directory -Force $lists | Out-Null
    $snap = Join-Path $LogDir "scripts-$Tag"
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-e16b.ps1, arms\bb-ctl-14.txt, arms\bb-val-2.txt, arms\e16b-widen.txt $snap -Force
    if (-not (Test-Path 'arms\e16b-widen.txt')) { throw 'arms\e16b-widen.txt, the widened sessions, is not written' }

    $train = @(Read-List 'arms\bb-ctl-14.txt')
    $val = @(Read-List 'arms\bb-val-2.txt')
    $widen = @(Read-List 'arms\e16b-widen.txt')

    # 1. The bright-cell lists: the fourteen sessions' (their export sample left out), the widened sessions' (all their
    #    cells), and the arm's list, the two together.
    $trainList = Join-Path $lists 'bright-train.txt'
    $widenList = Join-Path $lists 'bright-widen.txt'
    $armList = Join-Path $lists 'bright-arm.txt'
    if (-not (Test-Path $armList)) {
        if (Stop-Requested 'bright-cell lists') { return }
        Set-Status 'bright-cell lists'
        Invoke-Tianwen 'bright-cells (train)' (@('dataset', 'bright-cells', '--bake', $Bake, '--per-session', '45', '--seed', '1',
            '--exclude-sample', '120', '--export-seed', '1', '--out', $trainList) + @(Session-Args $train)) $log
        Invoke-Tianwen 'bright-cells (widen)' (@('dataset', 'bright-cells', '--bake', $Bake, '--per-session', '45', '--seed', '1',
            '--out', $widenList) + @(Session-Args $widen)) $log
        $cells = @(Get-Content $trainList, $widenList | Where-Object { $_ -and -not $_.StartsWith('#') })
        @("# E16b's arm: bright-train.txt and bright-widen.txt together") + $cells | Set-Content $armList -Encoding utf8
    }
    $armCells = @(Get-Content $armList | Where-Object { $_ -and -not $_.StartsWith('#') })
    $armSessions = @($armCells | ForEach-Object { ($_ -split "`t", 3)[2] } | Sort-Object -Unique)
    "bright cells for the arm: $($armCells.Count) over $($armSessions.Count) sessions (arms: $Arms)" | Tee-Object -FilePath $log -Append
    if ($Arms -eq 'both' -and ($armCells.Count -lt $MinArmCells -or $armSessions.Count -lt 3)) {
        throw "the widened pool holds $($armCells.Count) bright cells over $($armSessions.Count) sessions, under $MinArmCells over three; the arm waits on the owner (run -Arms control meanwhile)"
    }
    if ($Arms -eq 'both' -and $MinArmCells -lt 150) {
        "the arm runs on $($armCells.Count) bright cells, under the registered 150, by the owner's decision of 2026-09-30" | Tee-Object -FilePath $log -Append
    }

    # 2. One export: the control's sessions with their sample and the fourteen's bright cells, then the widened sessions'
    #    bright cells alone. The master anchor throughout, each session's noise shaped by its master's integration.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -ne 'done') {
        if (Stop-Requested 'export') { return }
        Set-Status 'export'
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $common = @('dataset', 'degrade', '--bake', $Bake, '--out', $Export, '--mode', 'noise', '--shape', 'warped',
            '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--draws', '8', '--seed', '1', '--noise-anchor', 'master-calibration')
        try {
            Invoke-Tianwen 'export (control sessions)' ($common + @('--cells', '120', '--extra-cells', $trainList) + @(Session-Args ($train + $val))) (Join-Path $Export 'degrade.log')
            Invoke-Tianwen 'export (widened sessions)' ($common + @('--cells', '120', '--extra-cells', $widenList, '--listed-only') + @(Session-Args $widen)) (Join-Path $Export 'degrade.log')
        }
        catch { "failed" | Out-File $exportStatus -Encoding utf8; throw }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }
    $failedSessions = @(Get-Content (Join-Path $Export 'degrade.log') | Select-String 'failed [1-9]')
    if ($failedSessions) { throw "the export failed sessions: $($failedSessions -join '; ')" }

    # 3. The two caches from the one export.
    $ctl = Join-Path $Scratch 'n2n-e16b-ctl'
    $arm = Join-Path $Scratch 'n2n-e16b-arm'
    $armTrain = Join-Path $lists 'arm-train.txt'
    # Every session of the arm list is a train session: the fourteen, and each widened one that gave a cell.
    @($train + @($armSessions | Where-Object { $train -notcontains $_ })) | Sort-Object -Unique | Set-Content $armTrain -Encoding utf8
    $prepares = @(@{ Cache = $ctl; Train = (Resolve-Path 'arms\bb-ctl-14.txt').Path; Extra = @() })
    if ($Arms -eq 'both') { $prepares += @{ Cache = $arm; Train = $armTrain; Extra = @('--extra-cells', $armList) } }
    foreach ($c in $prepares) {
        if (Test-Path (Join-Path $c.Cache 'meta.json')) { continue }
        if (Stop-Requested "prepare $($c.Cache)") { return }
        Set-Status "prepare $(Split-Path -Leaf $c.Cache)"
        & python n2n_smoke.py --prepare --root $Export --cache $c.Cache --train-from-list $c.Train `
            --val-from-list arms\bb-val-2.txt --cells-per-session 45 --val-cells-per-session 120 `
            --exclude-cells $armList @($c.Extra) *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $($c.Cache) failed (exit $LASTEXITCODE)" }
    }

    # D1 (as amended): the control holds E16a's cells, except in the two sessions whose P0 sample the recipe-3 store moved
    # (the Pleiades and Triangulum, 2 of 300 cells each, so their seeded picks differ), and on every shared cell its clean
    # tile is E16a's within 2 fp16 steps (4.9e-4 stretched, mean under 1e-5): the rounding the stores' own P0 master
    # tiles differ by.
    $d1 = & python e16b_d1.py (Join-Path $Scratch 'n2n-bb-ctl-rfm') $ctl
    "D1 control against n2n-bb-ctl-rfm: $d1" | Tee-Object -FilePath $log -Append
    if (-not "$d1".StartsWith('True')) { throw "D1 failed: $d1" }

    # D2. The injection's noise model against the half pairs.
    $d2 = Join-Path $LogDir "$Tag-d2.txt"
    if (-not (Test-Path $d2)) {
        if (Stop-Requested 'D2') { return }
        Set-Status 'D2 noise-check'
        & $Tianwen dataset noise-check --bake $Bake --cells 120 --seed 1 --extra-cells $armList --anchor master-calibration --bright-gate relative @(Session-Args ($train + $val + $armSessions | Sort-Object -Unique)) *> "$d2.partial"
        $d2Exit = $LASTEXITCODE
        Move-Item "$d2.partial" $d2 -Force
        Get-Content $d2 | Select-String '^\[noise-check\] (quiet|bright|PASS|FAIL)' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
        if ($d2Exit -ne 0) { throw "D2 failed (exit $d2Exit); see $d2" }
    }
    elseif (-not (Select-String -Path $d2 -Pattern '^\[noise-check\] PASS' -Quiet)) { throw "D2 failed earlier; see $d2" }

    # 4. The eval caches with their bright cells, then D3.
    $evalList = Join-Path $lists 'bright-eval.txt'
    $caches = @(
        @{ Name = 'n2n-bb-eval4'; Train = 'arms\bb-eval-train-2.txt'; Val = 'arms\bb-eval-4.txt'; Cells = 60
           Fields = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03', 'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19') },
        @{ Name = 'n2n-e2-eval4b'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4b-25full.txt'; Cells = 60
           Fields = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18', 'eta-Car-Nebula/2026-02-20') },
        @{ Name = 'n2n-eval4'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4-25full.txt'; Cells = 48
           Fields = @('Rim-Nebula/2025-05-02', 'Horsehead-Nebula/2025-10-28', 'Skull-and-Crossbones-Nebula/2026-02-14') })
    if (-not (Test-Path $evalList)) {
        if (Stop-Requested 'eval bright cells') { return }
        Set-Status 'eval bright cells'
        $evalIds = @($caches | ForEach-Object { Read-List $_.Val }) | Sort-Object -Unique
        Invoke-Tianwen 'bright-cells (eval)' (@('dataset', 'bright-cells', '--bake', $EvalBake, '--per-session', '20', '--seed', '1',
            '--frame', 'halfmaster_b', '--out', $evalList) + @(Session-Args $evalIds)) $log
    }
    foreach ($c in $caches) {
        $cache = Join-Path $Scratch "$($c.Name)-rfpcb"
        if (Test-Path (Join-Path $cache 'meta.json')) { continue }
        if (Stop-Requested "prepare $($c.Name)-rfpcb") { return }
        Set-Status "prepare $($c.Name)-rfpcb"
        & python n2n_smoke.py --prepare --root $EvalBake --cache $cache --train-from-list $c.Train `
            --val-from-list $c.Val --cells-per-session 5 --val-cells-per-session $c.Cells --extra-cells $evalList *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $($c.Name)-rfpcb failed (exit $LASTEXITCODE)" }
        # Every earlier cell is still there: the -rfpc cache's keys are the first of this one's val keys.
        $superset = & python -c "import sys, n2n_smoke as S; _, ma = S.open_cache(sys.argv[1]); _, mb = S.open_cache(sys.argv[2]); a, b = [tuple(k) for k in ma['keys']], [tuple(k) for k in mb['keys']]; print(set(a) <= set(b), len(a), len(b))" `
            (Join-Path $Scratch "$($c.Name)-rfpc") $cache
        "$($c.Name)-rfpcb holds every -rfpc cell (holds, before, after): $superset" | Tee-Object -FilePath $log -Append
        if (-not "$superset".StartsWith('True')) { throw "$($c.Name)-rfpcb lost cells of -rfpc: $superset" }
    }

    $env:TIANWEN_CLI = (Resolve-Path $Tianwen).Path
    $env:TIANWEN_BAKES = $EvalBake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir "gaia\solved-$(Split-Path -Leaf $EvalBake)"
    $shipped = "shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')"
    $d3Readable = @{ 2 = 0; 3 = 0 }
    foreach ($c in $caches) {
        foreach ($f in $c.Fields) {
            $out = Join-Path $LogDir "$Tag-d3-$($f -replace '[/\\ ]', '_').txt"
            if (-not (Test-Path $out)) {
                if (Stop-Requested "D3 $f") { return }
                Set-Status "D3 $f"
                & python n2n_starsplit.py --cache (Join-Path $Scratch "$($c.Name)-rfpcb") --models $shipped --only $f --per-session *> "$out.partial"
                if ($LASTEXITCODE -ne 0) { throw "D3 scoring $f failed (exit $LASTEXITCODE)" }
                Move-Item "$out.partial" $out -Force
            }
            # A level is readable where the reference line has numbers in its 0-1 px and 1-2 px bands, the bands the
            # predictions read.
            $ref = (Select-String -Path $out -Pattern 'detail kept gauss1 ref:' | Select-Object -First 1).Line
            $levels = ($ref -split 'ref:', 2)[1] -split '\|'
            foreach ($lvl in 2, 3) {
                $bands = $levels[$lvl] -split ','
                if ($bands[0].Trim() -ne '-' -and $bands[1].Trim() -ne '-') { $d3Readable[$lvl]++ }
            }
        }
    }
    "D3 fields readable: 0.45-0.60 on $($d3Readable[2]) (needs 6), 0.60 and up on $($d3Readable[3]) (needs 4)" | Tee-Object -FilePath $log -Append
    if ($d3Readable[2] -lt 6 -or $d3Readable[3] -lt 4) { throw "D3 failed: 0.45-0.60 readable on $($d3Readable[2]), 0.60 up on $($d3Readable[3])" }

    # 5. Train, interleaved so a stop leaves matched pairs.
    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        $runs = @(@{ Cache = $ctl; Name = "bb_e16bctl_s$seed" })
        if ($Arms -eq 'both') { $runs += @{ Cache = $arm; Name = "bb_e16barm_s$seed" } }
        foreach ($t in $runs) {
            if (Test-Path $stopFile) { "stop file present before $($t.Name)" | Tee-Object -FilePath $log -Append; break seeds }
            if (Test-Path (Join-Path $t.Cache "$($t.Name).pt")) { "train $($t.Name): present, skipped" | Tee-Object -FilePath $log -Append; continue }
            Set-Status "train $($t.Name)"
            "train $($t.Name) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $t.Cache --synthetic --loss l2 --upsample --cond-map `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
                --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
                --seed $seed --out "$($t.Name).pt" *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train $($t.Name) failed" }
        }
    }
    if (Stop-Requested 'scoring') { return }

    # 6. Score in both plane conditions on the -rfpcb caches.
    $mapped = @()
    foreach ($seed in 0..($SeedCount - 1)) {
        foreach ($m in @(@{ Label = "convmap3_s$seed"; Cache = $ctl; Name = "bb_e16bctl_s$seed" }, @{ Label = "convmapb_s$seed"; Cache = $arm; Name = "bb_e16barm_s$seed" })) {
            if (Test-Path (Join-Path $m.Cache "$($m.Name).pt")) { $mapped += "$($m.Label)=$(Final $m.Cache $m.Name)" }
        }
    }
    foreach ($seed in 0..3) { $mapped += "convmap_s$seed=$(Join-Path $Scratch "n2n-bb-ctl-rfm\bb_ctlmap_s$seed.pt")" }
    $others = @(foreach ($seed in 0..3) { "convrf_s$seed=$(Join-Path $Scratch "n2n-bb-ctl-rf\bb_ctlrf_s${seed}_final.pt")" }) + @($shipped)
    foreach ($m in $mapped + $others) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    "scoring with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" | Tee-Object -FilePath $log -Append
    # A control-only pass scores under its own name, so the full pass, once the arm exists, is never skipped as done.
    $scorePrefix = if ($Arms -eq 'both') { "$Tag-score" } else { "$Tag-ctl-score" }
    $failed = @()
    foreach ($cond in @('orc', 'est')) {
        $models = if ($cond -eq 'orc') { $mapped + $others } else { $mapped }
        $extra = @(if ($cond -eq 'orc') { '--plane-truth-anchor' })
        foreach ($c in $caches) {
            foreach ($f in $c.Fields) {
                $out = Join-Path $LogDir "$scorePrefix-$cond-$($c.Name)-$($f -replace '[/\\ ]', '_').txt"
                if (Test-Path $out) { continue }
                if (Stop-Requested "score $cond $f") { return }
                Set-Status "score $cond $($c.Name) $f"
                & python n2n_starsplit.py --cache (Join-Path $Scratch "$($c.Name)-rfpcb") --models @models --only $f --per-session @extra *> "$out.partial"
                if ($LASTEXITCODE -ne 0) { $failed += "$cond $f (exit $LASTEXITCODE)" } else { Move-Item "$out.partial" $out -Force }
            }
        }
    }
    $summary = if ($failed) { "scoring failed: $($failed -join ', ')" } else { 'checked, trained and scored in both conditions' }
    "done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
