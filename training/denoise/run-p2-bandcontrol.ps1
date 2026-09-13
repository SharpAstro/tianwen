# P2 E2.7: does the objective sharpen anything once it can SEE the scale being deblurred?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-06, after E2.6.
#
# WHY. E2.6 ran six seeds and no seed ever narrowed a star: best over 240 probes was 1.241 against a
#   1.382 input, the mean FINAL ratio was 1.444 (wider than the input), and width degraded
#   monotonically while L2 loss plateaued at 8e-5. I first wrote that up as MMSE regression, which
#   points at an architecture. Reading the loss says something cheaper: `--band-scales` names SIGMAS
#   (`_gauss_kernel(sigma)`), the run supervised sigma 2-4 and 4-8, and on this project's data the
#   stars are sigma 0.76-1.05, the probed input sigma 1.09-1.50 and the injected kernel sigma
#   0.78-1.07. Every relevant scale is BELOW the finest supervised band. What covers the stars is
#   plain MSE, which the background dominates, so trading star sharpness for background fidelity
#   LOWERS the loss. The exclusion of the fine band is documented in n2n_smoke.py, but it was measured
#   for the DENOISER's single-sub target (its 1-2 px band carries 5.18x the master's RMS) and this
#   regime's target is a clean master, where that argument does not hold.
#
# WHAT IT IS. Two arms x three seeds, against E2.6's six seeds as the baseline. Everything else is
#   byte-identical to the power-check recipe; only --band-scales moves.
#     A  "1,2 2,4 4,8"  the minimal addition. Note the loss divides by the band COUNT, so going from
#                       two bands to three drops each band's weight from 1.5 to 1.0: arm A tests the
#                       fix while DILUTING the very band it adds.
#     B  "1,2"          the concentrated version, fine band at the full weight of 3.0. Its only job is
#                       to make a null result in A attributable: A failing tells us nothing on its own
#                       about whether the band or its weight was the problem.
#   Seeds 0,1,2 are E2.6's own seeds, so the comparison is paired.
#
# DO NOT put "0,1" in --band-scales. Line 800 builds kernels for every sigma named with no `s > 0`
#   guard (unlike line 474, which has one), and _gauss_kernel(0) divides by zero. Supervising the
#   sub-pixel band needs a code change first.
#
# THE GATE IS STILL BROKEN and that is deliberate. E2.6 showed stars_kept demands [0.90, 1.10] while
#   the input itself scores 0.763, so every probe will FAIL and no checkpoint will be selected again.
#   That does not matter here: the gate only OBSERVES, it never touches training, so the width
#   trajectory is exactly as comparable to E2.6's as a fixed gate would make it. Fixing the gate
#   (task 13) proceeds on CPU while this runs. Read the trajectory, not the verdict.
#
# PREDICTION: in all three seeds of at least one arm, the minimum out/truth over the trajectory falls
#   below the 1.382 input, and the trajectory stops degrading with steps. Confidence: MODERATE. The
#   scale arithmetic is not in doubt, but "the loss can now see the scale" is not the same claim as
#   "an 0.81M-parameter net at 4000 steps can invert this blur", and only the second one ships.
# WHAT WOULD CHANGE THE PLAN:
#   - min ratio below 1.382 in all three seeds of an arm AND improving with steps: band placement was
#     the whole fix. Task 14 closes with no architecture change; re-run the power check on the fixed
#     recipe, since E2.6's sd was measured on models that all sat at the null and cannot be reused.
#   - min ratio below 1.382 but marginal (above 1.30), or still degrading with steps: partial. Raise
#     band_loss, or add the sub-pixel band behind the code fix above, BEFORE reaching for RL.
# KILL LINE: if neither arm's minimum beats E2.6's baseline minimum of 1.241, band placement is not
#   the constraint. Stop iterating on band weights entirely and go to unrolled Richardson-Lucy, which
#   carries the forward operator in the architecture rather than hoping a loss implies it. Two arms
#   exist precisely so this kill line can be believed: a single diluted arm failing would not earn it.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-p2-bandcontrol.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$Export = 'D:\Astro-Dataset\degraded\p2-blur',
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2)
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 0,1,2' arrives as the single string '0,1,2' and coerces to 12; use -Command instead."
    }
}

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'p2-band.status'
$log = Join-Path $LogDir 'p2-band.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-p2-band'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-p2-bandcontrol.ps1 $snap -Force
Copy-Item arms\e2-train-8.txt, arms\e2-val-2.txt $snap -Force

# The SAME cache E2.6 trained on, not a fresh export. The baseline is only a baseline if the data is
# identical; re-exporting would put a new draw between the two arms and the six seeds they answer to.
$cache = Join-Path $Scratch 'n2n-p2-blur'
if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
    throw "no cache at $cache. E2.6 built it; this control deliberately does not rebuild it, because a fresh export would break the paired comparison against E2.6's six seeds."
}

$arms = @(
    @{ Name = 'a'; Scales = '1,2 2,4 4,8'; Note = 'minimal addition, fine band diluted to weight 1.0' }
    @{ Name = 'b'; Scales = '1,2';         Note = 'concentrated, fine band at full weight 3.0' }
)

try {
    foreach ($arm in $arms) {
        "=== arm $($arm.Name): --band-scales `"$($arm.Scales)`"  ($($arm.Note))" | Tee-Object -FilePath $log -Append
        foreach ($seed in $Seeds) {
            $out = "p2band_$($arm.Name)_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train arm $($arm.Name) seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            "train arm $($arm.Name) seed $seed -> $out" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --cond-psf01 --loss l2 --upsample `
                --band-loss 3 --band-scales $arm.Scales --base 32 --steps 4000 --gate-every 100 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train failed for arm $($arm.Name) seed $seed" }
        }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
