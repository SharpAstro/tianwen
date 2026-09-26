# E13's last four scores (the n2n-eval4 fields), re-run by hand after run-pool.ps1 stopped at score 9 of 12
# on 2026-09-27: the 24mm ASI585 master needs a plate solve, the solve's astrometry.net probe hung on a
# wedged WSL service, and the scorer waited two hours on a pipe the stranded wsl.exe held (issue #977).
# The same scorer, models and flags as run-pool.ps1's loop; the CLI is this checkout's Debug build, which
# carries the bounded probe. A field that fails is recorded and the next one still runs. It ran once with
# the 24mm field too (2026-09-27 07:43, "no solution", as the 2026-09-05 run log said it would); that field
# is excluded for good, from here and from run-pool.ps1.
#
# Run DETACHED; read the status file, never the log. Job files: C:\temp\e2\e13rescore.{pid,status}.
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Cli = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Debug\net10.0\tianwen.exe"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$status = Join-Path $LogDir 'e13rescore.status'
$PID | Out-File (Join-Path $LogDir 'e13rescore.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }

$pool = Join-Path $Scratch 'n2n-pool'
$ctl = Join-Path $Scratch 'n2n-bb-ctl'
# Exactly the list run-pool.ps1 scored with (its Final picks _final.pt where a run wrote one).
$models = @(
    "pool_s0=$pool\pool_s0_final.pt", "pool_s1=$pool\pool_s1_final.pt", "pool_s2=$pool\pool_s2_final.pt",
    "poolb_s0=$pool\poolb_s0.pt", "poolb_s1=$pool\poolb_s1.pt", "poolb_s2=$pool\poolb_s2.pt",
    "pool48_s0=$pool\pool48_s0.pt", "pool48_s1=$pool\pool48_s1_final.pt", "pool48_s2=$pool\pool48_s2_final.pt",
    "conv_s0=$ctl\bb_ctlconv_s0_final.pt", "conv_s1=$ctl\bb_ctlconv_s1_final.pt",
    "conv_s2=$ctl\bb_ctlconv_s2_final.pt", "conv_s3=$ctl\bb_ctlconv_s3_final.pt",
    "ctl4k_s0=$ctl\bb_ctl_s0.pt", "ctl4k_s1=$ctl\bb_ctl_s1_final.pt", "ctl4k_s2=$ctl\bb_ctl_s2_final.pt",
    "ctl4k_s3=$ctl\bb_ctl_s3_final.pt", "ctl4k_s4=$ctl\bb_ctl_s4_final.pt", "ctl4k_s5=$ctl\bb_ctl_s5_final.pt")
foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }

Remove-Item Env:TIANWEN_BAKES, Env:TIANWEN_SOLVED_MASTERS -ErrorAction SilentlyContinue
$env:TIANWEN_CLI = (Resolve-Path $Cli).Path
$failed = @()
$n = 8
foreach ($f in @('RIM 135mm', 'Horsehead', 'Statue of Liberty')) {
    $n++
    Set-Status "score $n/11 n2n-eval4 $f"
    $out = Join-Path $LogDir "pool-score-n2n-eval4-$($f -replace '[/\\ ]', '_').txt"
    & python n2n_starsplit.py --cache (Join-Path $Scratch 'n2n-eval4') --models @models --only $f --per-session *> $out
    if ($LASTEXITCODE -ne 0) { $failed += "$f (exit $LASTEXITCODE)" }
}
$summary = if ($failed) { "failed: $($failed -join ', ')" } else { 'all four scored' }
"done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
