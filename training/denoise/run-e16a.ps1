# E16a, the per-pixel noise plane alone (docs/plans/denoiser-training.md, run log, "Detail kept" and E16a): E15's
# cells and recipe exactly, the model told the noise of every pixel instead of one number per tile.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the export and before any E16a checkpoint exists. Written 2026-09-28.
#
# WHY. Every model conditions on the tile's darkest-half MAD broadcast as one number. After the stretch a bright
#   core's noise is half the sky's or less, so it is told the sky's and smooths real filaments as noise (the 1:1
#   sheets, and detail kept at the brightest level: convrf 0.85 of the 1-2 px band at full strength, 0.46 to 0.65
#   on the Orion core). Given a smooth per-pixel plane with no retraining, convrf's brightest-level 1-2 px detail
#   on the Orion core rose from 0.58 to 0.88 at no cost in removal (n2n_sigmamap_probe.py).
#
# ARM. convmap: the E10 control cells (arms/bb-ctl-14.txt, val arms/bb-val-2.txt) re-exported by a build that
#   writes each draw's plane (tianwen dataset degrade, .sigma.f16: the draw's own noise, known, through
#   StretchedNoise.Plane), prepared as n2n-bb-ctl-rfm, whose tiles the script proves byte-identical to
#   n2n-bb-ctl-rf's, so the plane is the only difference from E15's convrf. run-converge.ps1's training line with
#   --cond-map in place of --cond, seeds 0 to 3, final weights.
#
# SCORED on the three -rfs eval caches (E14's ring-fixed caches, the same cells and tiles, with half-A planes
#   ESTIMATED from each retained master by tianwen dataset noise-planes) in TWO conditions, because the estimate
#   is off by a factor per field against the half pairs' own noise (median 1.31, 0.78 to 2.54; the plane's shape
#   holds):
#     orc  --plane-truth-anchor: each session's planes rescaled to the pair truth over its sky. The PRIMARY read:
#          whether conditioning on the true per-pixel noise keeps detail, apart from how well it is estimated.
#     est  the estimate as the product would compute it. Secondary: what the estimator's error costs today.
#   Beside convmap_s0..3, every model scored in E15: convrf_s0..3, conv_s0..3, the shipped e2_wide_s2 (their
#   scores do not depend on the condition, since only a --cond-map checkpoint reads a plane).
#
# PREDICTIONS (orc unless said; detail kept is n2n_starsplit's, against half B; R as E14 and E15):
#   (1) Detail kept at the brightest level (>= 0.60), 1-2 px band, FULL strength, mean over the two fields that
#       level is readable on (V1045 Ori, eta Car): convmap at least 0.92, against convrf's 0.85 and the shipped
#       model's 0.85. Confidence: moderate. KILL: below 0.88, no better than convrf handed the plane untrained,
#       which would say training on the plane buys nothing the plane alone does not.
#   (2) The same at 0.45-0.60 over its four readable fields: at least convrf + 0.03 (0.89 to 0.92). Confidence:
#       low to moderate.
#   (3) R within 3.0 points of convrf's 29.78. A tolerance, not a derived threshold: honest conditioning can move
#       removal either way on a field whose tile estimate over-read its noise. KILL: R lower by more than 5, detail
#       bought by not denoising.
#   (4) The frontier (stars and compact spent at matched removal) no worse than convrf's on all but at most two of
#       the fields both can read. Confidence: moderate.
#   (5) est against orc: on each field whose estimate reads high (factor above 1.2 in the run log's table), R
#       higher and brightest-level detail lower than orc's, since an over-read plane asks for more removal.
#       Direction only. Confidence: moderate to high.
#
# WHAT IT CANNOT SETTLE. Bright structure the model never saw (the training cells hold none above 0.6; E16b's
#   bright-cell sampling is the next arm); whether the estimator can be fixed (a separate piece of work, and the
#   precondition for shipping any --cond-map model); fields other than these eleven.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\e16a.pid.
#   $s = (Resolve-Path .\run-e16a.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between stages or seeds, create C:\temp\e2\e16a.stop; NEVER stop the process itself.
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'degraded\bb-ctl-sigma'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int]$SeedCount = 4
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'e16a.status'
$log = Join-Path $LogDir 'e16a.log'
$stopFile = Join-Path $LogDir 'e16a.stop'
$PID | Out-File (Join-Path $LogDir 'e16a.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
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
Set-Status 'starting'

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    $planes = git -C $PSScriptRoot log -1 --format=%cI --grep 'every degraded draw carries its per-pixel noise plane'
    if (-not $planes) { throw 'the plane-writing exporter is not in this checkout' }
    $head = git -C $PSScriptRoot log -1 --format=%cI
    $built = (Get-Item $Tianwen).LastWriteTime
    foreach ($t in @($planes, $head)) {
        if ($built -lt [datetimeoffset]::Parse($t).LocalDateTime) { throw "the CLI was built $($built.ToString('o')), before $t; rebuild TianWen.Cli in Release" }
    }
    "E16a convmap at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $($built.ToString('o'))" | Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-e16a'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-e16a.ps1, arms\bb-ctl-14.txt, arms\bb-val-2.txt $snap -Force

    # 1. Export, run-bb-arm.ps1's flags and seed, by a build that writes the planes.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -ne 'done') {
        if (Stop-Requested 'export') { return }
        Set-Status 'export'
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $sessionArgs = @()
        foreach ($sid in @(Read-List 'arms\bb-ctl-14.txt') + @(Read-List 'arms\bb-val-2.txt')) { $sessionArgs += '--session'; $sessionArgs += $sid }
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export --mode noise --shape warped `
            --warp-sigma 0.5 --draws 8 --cells 120 --seed 1 --measure-shape @sessionArgs *>> (Join-Path $Export 'degrade.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }

    # 2. Prepare, then prove the tiles are n2n-bb-ctl-rf's byte for byte, so the planes are the only difference.
    $cache = Join-Path $Scratch 'n2n-bb-ctl-rfm'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
        if (Stop-Requested 'prepare') { return }
        Set-Status 'prepare n2n-bb-ctl-rfm'
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\bb-ctl-14.txt --val-from-list arms\bb-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    }
    $same = & python -c "import sys, numpy as np, n2n_smoke as S; a, ma = S.open_cache(sys.argv[1]); b, mb = S.open_cache(sys.argv[2]); eq = ma['keys'] == mb['keys'] and all(np.array_equal(a[i:i + 32, :9], b[i:i + 32, :9]) for i in range(0, len(a), 32)); print(eq and mb.get('sigma_planes', 0) > 0, mb.get('sigma_planes', 0))" `
        (Join-Path $Scratch 'n2n-bb-ctl-rf') $cache
    "tiles against n2n-bb-ctl-rf, and planes: $same" | Tee-Object -FilePath $log -Append
    if (-not "$same".StartsWith('True')) { throw "n2n-bb-ctl-rfm is not n2n-bb-ctl-rf plus planes ($same)" }

    # 3. Train, run-converge.ps1's line with --cond-map in place of --cond.
    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        if (Test-Path $stopFile) { "stop file present before seed $seed" | Tee-Object -FilePath $log -Append; break seeds }
        $out = "bb_ctlmap_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train convmap seed $seed : present, skipped" | Tee-Object -FilePath $log -Append; continue }
        Set-Status "train convmap seed $seed"
        "train convmap seed $seed -> $out (--synthetic, --cond-map, --schedule plateau) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond-map `
            --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
            --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
            --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train convmap failed for seed $seed" }
    }
    if (Stop-Requested 'scoring') { return }

    # 4. Score in both plane conditions on the -rfs caches.
    $ctl = Join-Path $Scratch 'n2n-bb-ctl'
    $ctlrf = Join-Path $Scratch 'n2n-bb-ctl-rf'
    $mapped = @()
    foreach ($seed in 0..($SeedCount - 1)) {
        if (Test-Path (Join-Path $cache "bb_ctlmap_s$seed.pt")) { $mapped += "convmap_s$seed=$(Final $cache "bb_ctlmap_s$seed")" }
    }
    $others = @()
    foreach ($seed in 0..3) { $others += "convrf_s$seed=$(Final $ctlrf "bb_ctlrf_s$seed")" }
    foreach ($seed in 0..3) { $others += "conv_s$seed=$(Final $ctl "bb_ctlconv_s$seed")" }
    $others += "shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')"
    foreach ($m in $mapped + $others) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    $env:TIANWEN_CLI = (Resolve-Path ($Tianwen -replace 'tianwen\.dll$', 'tianwen.exe')).Path
    $env:TIANWEN_BAKES = $Bake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
    "scoring with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" | Tee-Object -FilePath $log -Append
    $fields = [ordered]@{
        'n2n-bb-eval4-rfs'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                                'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b-rfs' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                                'eta-Car-Nebula/2026-02-20')
        'n2n-eval4-rfs'     = @('Rim-Nebula/2025-05-02', 'Horsehead-Nebula/2025-10-28', 'Skull-and-Crossbones-Nebula/2026-02-14')
    }
    $failed = @()
    foreach ($cond in @('orc', 'est')) {
        # Only a --cond-map checkpoint reads a plane, so the other models are scored once, in orc.
        $models = if ($cond -eq 'orc') { $mapped + $others } else { $mapped }
        $extra = if ($cond -eq 'orc') { @('--plane-truth-anchor') } else { @() }
        $n = 0
        foreach ($c in $fields.Keys) {
            if (-not (Test-Path (Join-Path $Scratch "$c\meta.json"))) { throw "no cache $c under $Scratch (run-evalrf-planes.ps1)" }
            foreach ($f in $fields[$c]) {
                $n++
                Set-Status "score $cond $n/11 $c $f"
                & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f --per-session @extra `
                    *> (Join-Path $LogDir "e16a-score-$cond-$c-$($f -replace '[/\\ ]', '_').txt")
                if ($LASTEXITCODE -ne 0) { $failed += "$cond $c $f (exit $LASTEXITCODE)" }
            }
        }
    }
    $summary = if ($failed) { "scoring failed: $($failed -join ', ')" } else { 'trained and scored in both conditions' }
    "done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
