# E11, the step budget (docs/plans/denoiser-training.md, run log 2026-09-26 "what went wrong"): is the
# E2 / E9 recipe stopped before it has learned what it can?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before any 16000-step checkpoint exists. Written 2026-09-26.
#
# THE FINDING THIS TESTS. Every arm since E2 trains 4000 steps under a cosine schedule annealed to zero
#   at the last step (n2n_smoke.py: CosineAnnealingLR, T_max = --steps), and its val noise is still
#   falling when the schedule stops it: the shipped WIDE run's s0 and s1 read 0.87 / 0.89 at step 3000
#   and 0.86 / 0.88 at 4000, four of E10's six controls fell 0.01 to 0.02 in their last 1000 steps, and
#   the three WIDE seeds ended 0.75 / 0.86 / 0.88, a spread no recipe difference here has ever matched.
#   The one step study on record (ai-denoise-deconv.md, "s1600 / s2400") ran SHORTER, never longer, and
#   found that steps buy RANGE (the lowest noise a run reaches) while the trade at matched noise stays
#   put. E10's arm, 1.7x the cells in the same 4000 steps, lost exactly range (half the control's
#   full-strength removal).
#
# ARM. The E10 control cache (C:\temp\tianwen-scratch\n2n-bb-ctl: WIDE on 2026-09-25-full minus its three
#   test sessions, 630 train cells, val arms/bb-val-2.txt), the recipe byte for byte except --steps 16000
#   (so the cosine spans the whole run) and --gate-every 200. Four seeds, final weights (the _final.pt
#   where the gate passed, else the output, which is then final). Against E10's six 4000-step control
#   seeds, re-scored in the same pass so both sides read one metric.
#
# PRIMARY. R = the mean over all eight fields (bb-eval-4 and eval4b) of noise removed at full strength,
#   per seed; full strength needs no catalogue, so Carina counts here. The 4000-step controls read
#   R = 24.67, seed sd 3.67 (18.6 to 28.2). Gain G = mean R at 16000 minus 24.67, Welch 95 percent.
#   PREDICTION: G >= +8, with (a) stars and compact at matched removal (4 and 10 percent, per field, the
#   fixed split) no worse than the 4000-step spread, and (b) the seed sd of R under 3.67. Confidence:
#   moderate on the gain (the old study's range grew 10 to 20 points from 2400 to 4000 steps), low on
#   the spread.
#   KILL: the interval's upper bound is under +5. Then the recipe is effectively converged at 4000 on
#   this data and steps are not the lever; the next suspects are capacity (E6) and the pool.
#   Sizing: four seeds resolve d = 8 at 80 percent power against sd 3.67 (2 (1.96 + 0.84)^2 3.67^2 / 8^2
#   = 3.3); a gain as small as 5 is not claimable from this run.
#
# SCORED WITH the metric as fixed by the 2026-09-26 audit (n2n_starsplit.py: a session whose Gaia-
#   confirmed fraction does not clear its coincidence floor is dropped from the split, and an unmatched
#   peak beside a catalogued star is a blend, never extended). The log records the scoring script's hash.
#
# WHAT IT CANNOT SETTLE. Whether more DATA helps once runs converge (E10's arm at 16000 steps is the
#   follow-up), and whether the gain holds on masters outside these eight fields.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\steps.pid.
#   $s = (Resolve-Path .\run-steps.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int]$SeedCount = 4,
    [int]$Steps = 16000,
    [switch]$ScoreOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir 'steps.status'
$log = Join-Path $LogDir 'steps.log'
$PID | Out-File (Join-Path $LogDir 'steps.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
Set-Status 'starting'

try {
    "E11 steps at $(git -C $PSScriptRoot rev-parse --short HEAD), $Steps steps, seeds 0..$($SeedCount - 1)" | Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-steps'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-steps.ps1 $snap -Force
    $cache = Join-Path $Scratch 'n2n-bb-ctl'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) { throw "no cache $cache (run-bb-arm.ps1 prepares it)" }
    $tag = "bb_ctl$([int]($Steps / 1000))k"

    # 1. Train.
    if (-not $ScoreOnly) {
        foreach ($seed in 0..($SeedCount - 1)) {
            $out = "${tag}_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            Set-Status "train $Steps steps seed $seed"
            "train seed $seed -> $out (--synthetic, $Steps steps)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps $Steps --gate-every 200 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train failed for seed $seed" }
        }
    }

    # 2. Score, at final weights, the long seeds beside E10's six 4000-step controls.
    function Final([string]$name) {
        $f = Join-Path $cache "${name}_final.pt"
        if (Test-Path $f) { $f } else { Join-Path $cache "$name.pt" }
    }
    $models = @(0..5 | ForEach-Object { "ctl4k_s$_=$(Final "bb_ctl_s$_")" }) +
              @(0..($SeedCount - 1) | ForEach-Object { "ctl16k_s$_=$(Final "${tag}_s$_")" })
    foreach ($m in $models) { $p = $m.Split('=', 2)[1]; if (-not (Test-Path $p)) { throw "no checkpoint $p" } }
    "scoring with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" | Tee-Object -FilePath $log -Append
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
                *> (Join-Path $LogDir "steps-score-$c-$($f -replace '[/\\]', '_').txt")
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
