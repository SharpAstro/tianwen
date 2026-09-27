# E15, E12's recipe on data that matches inference (docs/plans/denoiser-training.md, run log, E15): the E10
# control cache (n2n-bb-ctl) re-exported after the canvas-ring fix (issue #489; the commit "fix(ai): the NAFNet
# pre-stretch measures covered pixels only, and hands the canvas ring back untouched") as n2n-bb-ctl-rf, then
# E12's converged recipe on it, byte for byte. The ship candidate: E12's recipe is the best by R in both
# domains, and every E12 checkpoint learned on the pre-fix stretch.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the export and before any E15 checkpoint exists. Written 2026-09-27.
#
# WHAT CHANGES. The same 16 sessions (arms/bb-ctl-14.txt, val arms/bb-val-2.txt) from the same bake
#   (2026-09-25-full) with run-bb-arm.ps1's degrade flags and seed, so the cells are the same cells (checked
#   below: the prepared cache's cell keys must equal n2n-bb-ctl's, or the run stops before training). Only a
#   ringed master's stretch differs. The ring census of 2026-09-26 moves 9 of the 16: 8 of the 14 training
#   sessions (the seven 2024 Vela SNR panels on IDAS LPS-D3, broadband, and the Pleiades) and Triangulum,
#   the OBSERVED val session; the gate session HIP-80609 and the six narrowband training sessions keep a floor
#   inside the covered area and are unchanged. So convrf against conv, seed for seed, is the fix on E12's
#   recipe, as E14 was on the pool's.
#
# ARM. convrf: run-converge.ps1's training line exactly (--synthetic, l2, upsample, cond, band loss 3 at
#   "2,4 4,8", base 32, --schedule plateau, 60000-step cap), seeds 0 to 3, final weights, on n2n-bb-ctl-rf.
#
# SCORED on E14's eleven ring-fixed fields (the *-rf caches, identical tiles to E14's rf pass), every model in
#   one pass with the fixed split: convrf_s0..3, conv_s0..3, poolrf_s0..2, pool_s0..2, ctl4k_s0..5, and the
#   shipped model's checkpoint (e2_wide_s2.pt, the gate pick exported as tianwen_denoise_osc_e2wide_s2.onnx)
#   as a reference that no prediction is about. Every comparison is read WITHIN this pass; E14's rf values are
#   quoted below for orientation only (the CLI is rebuilt at this HEAD, so a reference can move slightly).
#
# PRIMARY. R = mean full-strength removal over the eleven fields, per seed (E14's definition). Threshold as
#   E13 and E14: 2.8 x conv's seed sd in E14's rf pass (4.53) x sqrt(2/4) = 8.97 points of R. Four seeds
#   resolve that and no less; no difference inside it is claimed.
# PREDICTIONS:
#   (1) convrf's R is no lower than conv's (29.18 in E14's rf pass) by more than 8.97. Confidence: moderate
#       to high (E14: poolrf 1.23 under pool against a 2.04 threshold). KILL: lower by more, which would say
#       the fixed stretch costs E12's recipe removal; no post-fix model of this recipe ships until that is
#       understood.
#   (2) The frontier (stars and compact spent at matched removal, 4 and 10 percent) is no worse than conv's
#       on all but at most two of the fields both arms can read (every seed of both reaching 4 percent).
#       Confidence: moderate.
#   (3) convrf's R stays above poolrf's (22.52 in E14's rf pass), so the recipe to ship stays E12's.
#       Confidence: moderate.
#   (4) convrf's seed sd of R is under 1.5 x conv's (6.8): the fix does not make this recipe seed-fragile, as
#       poolrf's sd rose to 5.5 x pool's. Confidence: low.
#   (5) Whatever moves, moves most on the four bb-eval-4 fields, because the moved training sessions are the
#       broadband ones. Confidence: low; reported per field either way.
#
# WHAT IT CANNOT SETTLE. A difference under about 9 points of R. Whether convrf should replace the shipped
#   model: that is the frontier against e2_wide_s2 on these fields plus a look at 1:1 (and the export parity),
#   a decision for after the read. Unseen sensors, as every arm before it.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\e15.pid.
#   $s = (Resolve-Path .\run-e15.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between stages or seeds, create C:\temp\e2\e15.stop; NEVER stop the process itself (it owns its
# child's output pipe, and stopping it kills the seed in flight).
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'degraded\bb-ctl-ringfix'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int]$SeedCount = 4
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'e15.status'
$log = Join-Path $LogDir 'e15.log'
$stopFile = Join-Path $LogDir 'e15.stop'
$PID | Out-File (Join-Path $LogDir 'e15.pid') -Encoding ascii
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
    # The binary must contain the fix and be this checkout's: built after the fix and after HEAD's commit.
    $fix = git -C $PSScriptRoot log -1 --format=%cI --grep 'the NAFNet pre-stretch measures covered pixels only'
    if (-not $fix) { throw 'the ring-fix commit is not in this checkout' }
    $head = git -C $PSScriptRoot log -1 --format=%cI
    $built = (Get-Item $Tianwen).LastWriteTime
    foreach ($t in @($fix, $head)) {
        if ($built -lt [datetimeoffset]::Parse($t).LocalDateTime) {
            throw "the CLI was built $($built.ToString('o')), before $t; rebuild TianWen.Cli in Release"
        }
    }
    "E15 convrf at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $($built.ToString('o')), ring fix committed $fix" |
        Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-e15'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-e15.ps1, arms\bb-ctl-14.txt, arms\bb-val-2.txt $snap -Force

    # 1. Export: the 16 sessions with run-bb-arm.ps1's flags and seed. Cells are seeded per session NAME, so
    #    exporting 16 of that export's 26 sessions gives the same cells. A finished export is skipped on re-run.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -eq 'done') {
        "export: $Export already done, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        if (Stop-Requested 'export') { return }
        Set-Status 'export'
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $sessions = @(Read-List 'arms\bb-ctl-14.txt') + @(Read-List 'arms\bb-val-2.txt')
        $sessionArgs = @()
        foreach ($sid in $sessions) { $sessionArgs += '--session'; $sessionArgs += $sid }
        "export -> $Export ($($sessions.Count) sessions, warped, --warp-sigma 0.5, seed 1) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export --mode noise --shape warped `
            --warp-sigma 0.5 --draws 8 --cells 120 --seed 1 --measure-shape @sessionArgs *>> (Join-Path $Export 'degrade.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }

    # 2. Prepare n2n-bb-ctl-rf with run-bb-arm.ps1's control selection, then prove the cells are n2n-bb-ctl's.
    $cache = Join-Path $Scratch 'n2n-bb-ctl-rf'
    if (Test-Path (Join-Path $cache 'meta.json')) {
        "prepare: $cache present, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        if (Stop-Requested 'prepare') { return }
        Set-Status 'prepare n2n-bb-ctl-rf'
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\bb-ctl-14.txt --val-from-list arms\bb-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    }
    $same = & python -c "import json,sys; a=json.load(open(sys.argv[1])); b=json.load(open(sys.argv[2])); print(a['keys']==b['keys'] and a['train_sessions']==b['train_sessions'] and a['val_sessions']==b['val_sessions'], len(a['keys']), len(b['keys']))" `
        (Join-Path $Scratch 'n2n-bb-ctl\meta.json') (Join-Path $cache 'meta.json')
    "cells against n2n-bb-ctl: $same" | Tee-Object -FilePath $log -Append
    if (-not "$same".StartsWith('True')) { throw "n2n-bb-ctl-rf does not hold n2n-bb-ctl's cells ($same); the contrast would not be the fix alone" }

    # 3. Train, E12's line exactly (run-converge.ps1).
    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        if (Test-Path $stopFile) { "stop file present before seed $seed" | Tee-Object -FilePath $log -Append; break seeds }
        $out = "bb_ctlrf_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) { "train convrf seed $seed : present, skipped" | Tee-Object -FilePath $log -Append; continue }
        Set-Status "train convrf seed $seed"
        "train convrf seed $seed -> $out (--synthetic, --schedule plateau) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond `
            --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
            --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
            --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train convrf failed for seed $seed" }
    }
    if (Stop-Requested 'scoring') { return }

    # 4. Score every model in one pass on E14's ring-fixed caches (run-poolrf-score.ps1 -Domain rf's fields).
    $ctl = Join-Path $Scratch 'n2n-bb-ctl'
    $models = @()
    foreach ($seed in 0..($SeedCount - 1)) {
        if (Test-Path (Join-Path $cache "bb_ctlrf_s$seed.pt")) { $models += "convrf_s$seed=$(Final $cache "bb_ctlrf_s$seed")" }
    }
    foreach ($seed in 0..3) { $models += "conv_s$seed=$(Final $ctl "bb_ctlconv_s$seed")" }
    foreach ($seed in 0..2) { $models += "poolrf_s$seed=$(Final (Join-Path $Scratch 'n2n-pool-rf') "poolrf_s$seed")" }
    foreach ($seed in 0..2) { $models += "pool_s$seed=$(Final (Join-Path $Scratch 'n2n-pool') "pool_s$seed")" }
    foreach ($seed in 0..5) { $models += "ctl4k_s$seed=$(Final $ctl "bb_ctl_s$seed")" }
    $models += "shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')"
    foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    $env:TIANWEN_CLI = (Resolve-Path ($Tianwen -replace 'tianwen\.dll$', 'tianwen.exe')).Path
    $env:TIANWEN_BAKES = $Bake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
    "scoring $($models.Count) models with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" |
        Tee-Object -FilePath $log -Append
    $fields = [ordered]@{
        'n2n-bb-eval4-rf'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                               'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b-rf' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                               'eta-Car-Nebula/2026-02-20')
        'n2n-eval4-rf'     = @('Rim-Nebula/2025-05-02', 'Horsehead-Nebula/2025-10-28', 'Skull-and-Crossbones-Nebula/2026-02-14')
    }
    $n = 0
    $failed = @()
    foreach ($c in $fields.Keys) {
        if (-not (Test-Path (Join-Path $Scratch "$c\meta.json"))) { throw "no cache $c under $Scratch" }
        foreach ($f in $fields[$c]) {
            $n++
            Set-Status "score $n/11 $c $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f --per-session `
                *> (Join-Path $LogDir "e15-score-$c-$($f -replace '[/\\ ]', '_').txt")
            if ($LASTEXITCODE -ne 0) { $failed += "$c $f (exit $LASTEXITCODE)" }
        }
    }
    $summary = if ($failed) { "scoring failed: $($failed -join ', ')" } else { 'trained and all eleven scored' }
    "done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
