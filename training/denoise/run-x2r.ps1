# Arm X2R (docs/plans/denoiser-training.md H8): the X2 contrast with the depth asymmetry flipped.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the first seed. Written 2026-09-06, after the X2 cache was measured and BEFORE
# x2ctl had finished, so the arm is not chosen on the strength of the result it is checking.
#
# WHY. X2 holds the input distribution fixed and moves only the target, which is the one axis H8 is
#   about -- except that a cross-night target brings its NIGHT with it. Measured on the x2 cache: the
#   target night is 1.2 to 4.7 times noisier than the input's night in every pair, median 2.25x, since
#   these nights differ in depth and transparency as well as in date. N2N stays unbiased under a
#   noisier target; its GRADIENT VARIANCE does not, and at a fixed 4000 steps that is a second axis.
#   Reversing every pair puts the deeper night on the target side and takes the median ratio from 2.25
#   to 1.19, so the independent target is now about as quiet as the shared one.
#
# PREDICTION: if X2 lost to x2ctl because of the target's noise, X2R closes most of that gap; if it lost
#   because a cross-night target hands the model a night-specific structured field to learn (the
#   three-frame measurement puts 5.5 to 18.6 percent of a night's own deviation in that term), X2R
#   loses by about as much as X2 did. Confidence in the second: MEDIUM, because the X2 seeds do not
#   merely underperform, they AMPLIFY (1.02 to 1.07x on the gate, faint amplitude above 1), which is
#   what learning a systematic looks like rather than what a noisy gradient looks like.
# KILL for H8: X2R is no better than its own control across three seeds. Together with X2 that is an
#   independent target losing at BOTH signs of the depth asymmetry, which refutes H8 rather than
#   parking it again.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-x2r.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
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
$status = Join-Path $LogDir 'x2r.status'
$log = Join-Path $LogDir 'x2r.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-x2r'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-x2r.ps1 $snap -Force
Copy-Item arms\x2r-*.txt $snap -Force

try {
    $cache = Join-Path $Scratch 'n2n-x2r'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
        throw "no prepared cache at $cache; prepare it from D:\Astro-Dataset\pairs-x2r first"
    }
    foreach ($arm in @('x2r', 'x2rctl')) {
        $target = if ($arm -eq 'x2r') { 'half-b' } else { 'half-a' }
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
