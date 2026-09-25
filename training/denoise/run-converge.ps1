# E12, train to convergence (docs/plans/denoiser-training.md, run log 2026-09-26): let each run decide its
# own length instead of stopping at a step count chosen in advance.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before any E12 checkpoint exists. Written 2026-09-26.
#
# THE FINDING THIS TESTS. Every recipe before this annealed a cosine to zero at 4000 steps, and E11's one
#   partial seed (16000-step cosine, stopped at step ~4800 by an operator error) showed what that cost:
#   same seed, same cache, val noise at step 4000 read 0.72x under the long schedule against 0.85x under
#   the 4000-step one, and passed the gate there, below every 4000-step control's FINAL value (0.76 to
#   0.85). 4000 was never measured; the one step study ran shorter runs only.
#
# ARM. The E10 control cache (n2n-bb-ctl: WIDE on 2026-09-25-full minus its test sessions, 630 train
#   cells, val arms/bb-val-2.txt) and the recipe byte for byte, except the schedule: --schedule plateau
#   holds lr 2e-4, scores the TRAINING objective on 240 fixed val cells x 2 fixed draws every 500 steps,
#   judges the mean of the last 3 scores, halves the rate after 4 such scores without a 0.1 percent gain,
#   and stops at the plateau after the 4th halving (n2n_smoke.py, "E12"). --steps 60000 is only a cap;
#   a run that reaches it says so and is reported as unconverged. Four seeds, final weights.
#
# PRIMARY. R = the mean over all eight fields (bb-eval-4 and eval4b) of noise removed at full strength,
#   per seed, against E10's six 4000-step controls re-scored in the same pass (R = 24.67, seed sd 3.67).
#   PREDICTION: (1) every seed runs past 12000 steps before the criterion stops it; (2) G = mean R minus
#   24.67 is at least +8; (3) stars and compact at matched removal (4 and 10 percent, per field, the fixed
#   split) are no worse than the 4000-step spread; (4) the seed sd of R is under 3.67. Confidence: high on
#   (1), moderate on (2) (E11's partial seed), low on (4).
#   KILL: the 95 percent interval of G has its upper bound under +5, or (3) fails on two or more fields.
#   Either way the converged model is not the better one this recipe can make.
#   Sizing as E11: four seeds resolve d = 8 against sd 3.67.
#
# SCORED WITH the fixed split (floor rule, blend rule); the log records the scoring script's hash.
#
# WHAT IT CANNOT SETTLE. Whether the plateau rule's own constants (window 3, patience 4, four halvings)
#   stop too early or too late; whether more data helps once runs converge (E10's arm cache, converged, is
#   the follow-up); masters outside these eight fields.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\converge.pid.
#   $s = (Resolve-Path .\run-converge.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it cleanly, create C:\temp\e2\converge.stop: it finishes the seed in flight, scores what exists
# and exits. NEVER stop this process to end it early: it owns its child's output pipe, and stopping it
# kills the child mid-seed (how E11's seed 0 was lost).
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int]$SeedCount = 4,
    [switch]$ScoreOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'converge.status'
$log = Join-Path $LogDir 'converge.log'
$stopFile = Join-Path $LogDir 'converge.stop'
$PID | Out-File (Join-Path $LogDir 'converge.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
Set-Status 'starting'

try {
    "E12 converge at $(git -C $PSScriptRoot rev-parse --short HEAD), seeds 0..$($SeedCount - 1)" | Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-converge'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-converge.ps1 $snap -Force
    $cache = Join-Path $Scratch 'n2n-bb-ctl'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) { throw "no cache $cache (run-bb-arm.ps1 prepares it)" }

    # 1. Train until each run's held-out objective stops improving.
    $trained = @()
    if (-not $ScoreOnly) {
        foreach ($seed in 0..($SeedCount - 1)) {
            if (Test-Path $stopFile) {
                "stop file present before seed $seed; training ends here" | Tee-Object -FilePath $log -Append
                break
            }
            $out = "bb_ctlconv_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                $trained += $seed
                continue
            }
            Set-Status "train to convergence seed $seed"
            "train seed $seed -> $out (--synthetic, --schedule plateau)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
                --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train failed for seed $seed" }
            $trained += $seed
        }
    }
    else {
        $trained = @(0..($SeedCount - 1) | Where-Object { Test-Path (Join-Path $cache "bb_ctlconv_s$_.pt") })
    }

    # 2. Score, at final weights, the converged seeds beside E10's six 4000-step controls.
    function Final([string]$name) {
        $f = Join-Path $cache "${name}_final.pt"
        if (Test-Path $f) { $f } else { Join-Path $cache "$name.pt" }
    }
    $models = @(0..5 | ForEach-Object { "ctl4k_s$_=$(Final "bb_ctl_s$_")" }) +
              @($trained | ForEach-Object { "conv_s$_=$(Final "bb_ctlconv_s$_")" })
    foreach ($m in $models) { $p = $m.Split('=', 2)[1]; if (-not (Test-Path $p)) { throw "no checkpoint $p" } }
    "scoring $($models.Count) models with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" |
        Tee-Object -FilePath $log -Append
    $fields = [ordered]@{
        'n2n-bb-eval4'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                            'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                            'eta-Car-Nebula/2026-02-20')
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
            Set-Status "score $n/8 $c $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f --per-session `
                *> (Join-Path $LogDir "converge-score-$c-$($f -replace '[/\\]', '_').txt")
            if ($LASTEXITCODE -ne 0) { throw "score failed on $c $f (exit $LASTEXITCODE)" }
        }
    }
    "done $(Get-Date -Format o): $($trained.Count) seeds trained" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
