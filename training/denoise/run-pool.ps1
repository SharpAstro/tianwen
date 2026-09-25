# E13, the varied pool (docs/plans/denoiser-training.md, run log 2026-09-26): what a model meant for any
# sensor needs once runs converge. Data, signal and features, one arm each, all trained to convergence.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before its export. Written 2026-09-26.
#
# WHY. Every arm so far trained on at most 17 sessions of three cameras at ONE injected noise shape, and
#   every verdict on data, conditioning and capacity was read at the 4000-step cosine that stopped runs
#   still learning (E11, E12). Two of those verdicts were about exactly the levers a varied pool needs:
#   band conditioning was rejected because it "converges far more slowly" and its planes were "nearly
#   redundant, because level dominates shape" (on a pool whose injected shape never varied), and capacity
#   was tested only on N2N pairs. The user's point (2026-09-26): more variety needs more signal and more
#   features.
#
# DATA. arms/pool-train.txt: 81 train-split colour sessions of 2026-09-25-full on 7 cameras (ASI533MC 53,
#   SV605CC 9, ASI585MC 9, ASI294MC 5, QHY294 2, Uranus-C 2, ASI462MC 1), minus pier-side views, comets,
#   the val pair, and every session sharing a night and a camera with an eval session (five eval sessions
#   are themselves in this bake's train split). 45 cells each (exported 48), val arms/bb-val-2.txt at 120.
#   NOISE SHAPE VARIES PER DRAW (tianwen dataset degrade --white-fraction 0.25 --warp-sigma 0 --warp-sigma-max
#   0.7): measured on this bake, white reads band1/band0 0.218, warped 0 / 0.5 / 0.7 read 0.314 / 0.433 /
#   0.653, against real masters at 0.26 (this Lanczos bake) to 0.62 (the bilinear one). Every other export
#   flag as E10's.
#
# ARMS, all E12's recipe (--schedule plateau, the same constants, cap 80000 steps), seeds interleaved:
#   pool    base 32, --cond (one level plane): the new recipe.
#   poolb   base 32, --cond-bands (three band-noise planes): more SIGNAL.
#   pool48  base 48, --cond: more FEATURES (2.3x the parameters).
#   Three seeds each, final weights, scored on twelve fields (eval4, eval4b, bb-eval-4) with the fixed
#   split beside E12's converged control seeds (conv_s*), the reference, and E10's 4000-step controls.
#
# PREDICTIONS (R = full-strength removal averaged per field; the frontier = stars and compact spent at
#   matched removal, 4 and 10 percent, per field):
#   pool vs E12:   R at least E12's on the four bb-eval-4 fields, whose masters share the injection's new
#                  low end, and within E12's seed spread elsewhere, frontier no worse. Two changes at once
#                  (data and shape); stated, not separated. Confidence: low to moderate.
#   poolb vs pool: better frontier or higher R at an equal one on at least 8 of the 12 fields. KILL: inside
#                  pool's seed spread on both, and then shape-reading conditioning buys nothing even where
#                  the shape varies. Confidence: low.
#   pool48 vs pool: better frontier on at least 8 of 12. KILL: inside pool's seed spread, and capacity is not
#                  the lever at 81 sessions. Confidence: low.
#   Sizing: three seeds per arm is a first read; E12 measures the converged seed spread, and no difference
#   is claimed unless it clears 2.8 times that spread times sqrt(2/3).
#
# WHAT IT CANNOT SETTLE. Unseen sensors: every eval camera is in the pool on other nights, so this measures
#   variety, not generalisation to a camera the model never met; that needs a camera held out entirely.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\pool.pid.
#   $s = (Resolve-Path .\run-pool.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# It exports and prepares at once (CPU), then waits for E12 (C:\temp\e2\converge.status) to read done
# before training, and gives up if E12 did not finish cleanly. To stop it between runs, create
# C:\temp\e2\pool.stop; NEVER stop the process itself (it owns its child's output pipe).
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Export = 'D:\Astro-Dataset\degraded\pool-vshape',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int]$SeedCount = 3,
    [switch]$ExportOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'pool.status'
$log = Join-Path $LogDir 'pool.log'
$stopFile = Join-Path $LogDir 'pool.stop'
$PID | Out-File (Join-Path $LogDir 'pool.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
Set-Status 'starting'

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    "E13 pool at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $((Get-Item $Tianwen).LastWriteTime.ToString('o'))" |
        Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-pool'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-pool.ps1 $snap -Force
    Copy-Item arms\pool-train.txt, arms\bb-val-2.txt $snap -Force

    # 1. Export: the val pair at 120 cells, then the pool at 48 (the trainer takes 45 per train session),
    #    one varied-shape setting for both, into one store. A finished stage is skipped on re-run.
    $shapeArgs = @('--mode', 'noise', '--shape', 'warped', '--warp-sigma', '0', '--warp-sigma-max', '0.7',
                   '--white-fraction', '0.25', '--draws', '8', '--seed', '1')
    New-Item -ItemType Directory -Force $Export | Out-Null
    foreach ($stage in @(@{ Name = 'val'; List = 'arms\bb-val-2.txt'; Cells = 120 },
                         @{ Name = 'pool'; List = 'arms\pool-train.txt'; Cells = 48 })) {
        $stageStatus = Join-Path $Export "degrade-$($stage.Name).status"
        if ((Test-Path $stageStatus) -and ((Get-Content $stageStatus -Raw).Trim() -eq 'done')) {
            "export $($stage.Name): done, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        Set-Status "export $($stage.Name)"
        $sessionArgs = @()
        foreach ($sid in (Read-List $stage.List)) { $sessionArgs += '--session'; $sessionArgs += $sid }
        $measure = if ($stage.Name -eq 'pool') { @('--measure-shape') } else { @() }
        "export $($stage.Name) -> $Export ($($sessionArgs.Count / 2) sessions, $($stage.Cells) cells)" | Tee-Object -FilePath $log -Append
        "running" | Out-File $stageStatus -Encoding utf8
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export @shapeArgs --cells $stage.Cells @measure @sessionArgs `
            *>> (Join-Path $Export "degrade-$($stage.Name).log")
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $stageStatus -Encoding utf8; throw "export $($stage.Name) failed (exit $LASTEXITCODE)" }
        "done" | Out-File $stageStatus -Encoding utf8
        Get-Content (Join-Path $Export "degrade-$($stage.Name).log") | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } |
            Tee-Object -FilePath $log -Append
    }

    # 2. Prepare the one cache all three arms train from.
    $cache = Join-Path $Scratch 'n2n-pool'
    if (Test-Path (Join-Path $cache 'meta.json')) {
        "prepare: $cache present, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        Set-Status 'prepare n2n-pool'
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\pool-train.txt --val-from-list arms\bb-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    }
    if ($ExportOnly) { "done (export only) $(Get-Date -Format o)" | Out-File $status -Encoding utf8; return }

    # 3. Wait for E12 to hand over the GPU, and to have finished cleanly: its converged spread sizes the read.
    $e12 = Join-Path $LogDir 'converge.status'
    while ((Test-Path $e12) -and ((Get-Content $e12 -Raw).Trim() -like 'running*')) {
        Set-Status 'waiting for E12 (converge) to finish'
        Start-Sleep 300
    }
    if (-not ((Test-Path $e12) -and ((Get-Content $e12 -Raw).Trim() -like 'done*'))) {
        "stopped $(Get-Date -Format o): E12 did not finish cleanly, so E13 does not train" | Out-File $status -Encoding utf8
        return
    }

    # 4. Train the three arms to convergence, seeds interleaved.
    $arms = [ordered]@{
        pool   = @('--base', '32', '--cond')
        poolb  = @('--base', '32', '--cond-bands')
        pool48 = @('--base', '48', '--cond')
    }
    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        foreach ($a in $arms.Keys) {
            if (Test-Path $stopFile) { "stop file present before $a seed $seed" | Tee-Object -FilePath $log -Append; break seeds }
            $out = "${a}_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) { "train $a seed $seed : present, skipped" | Tee-Object -FilePath $log -Append; continue }
            Set-Status "train $a seed $seed"
            "train $a seed $seed -> $out ($($arms[$a] -join ' '), --schedule plateau)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample @($arms[$a]) `
                --band-loss 3 --band-scales "2,4 4,8" --schedule plateau --steps 80000 `
                --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train $a failed for seed $seed" }
        }
    }

    # 5. Score every final-weight checkpoint on twelve fields beside E12's and E10's controls.
    function Final([string]$dir, [string]$name) {
        $f = Join-Path $dir "${name}_final.pt"
        if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
    }
    $ctl = Join-Path $Scratch 'n2n-bb-ctl'
    $models = @()
    foreach ($a in $arms.Keys) {
        foreach ($seed in 0..($SeedCount - 1)) {
            if (Test-Path (Join-Path $cache "${a}_s$seed.pt")) { $models += "${a}_s$seed=$(Final $cache "${a}_s$seed")" }
        }
    }
    foreach ($seed in 0..3) {
        if (Test-Path (Join-Path $ctl "bb_ctlconv_s$seed.pt")) { $models += "conv_s$seed=$(Final $ctl "bb_ctlconv_s$seed")" }
    }
    $models += @(0..5 | ForEach-Object { "ctl4k_s$_=$(Final $ctl "bb_ctl_s$_")" })
    "scoring $($models.Count) models with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" |
        Tee-Object -FilePath $log -Append
    $fields = [ordered]@{
        'n2n-bb-eval4'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                            'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                            'eta-Car-Nebula/2026-02-20')
        'n2n-eval4'     = @('24mm ASI585', 'RIM 135mm', 'Horsehead', 'Statue of Liberty')
    }
    $n = 0
    foreach ($c in $fields.Keys) {
        if ($c -eq 'n2n-bb-eval4') {
            $env:TIANWEN_BAKES = $Bake
            $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
        }
        else {
            Remove-Item Env:TIANWEN_BAKES, Env:TIANWEN_SOLVED_MASTERS -ErrorAction SilentlyContinue
        }
        foreach ($f in $fields[$c]) {
            $n++
            Set-Status "score $n/12 $c $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f --per-session `
                *> (Join-Path $LogDir "pool-score-$c-$($f -replace '[/\\ ]', '_').txt")
            if ($LASTEXITCODE -ne 0) { throw "score failed on $c $f (exit $LASTEXITCODE)" }
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
