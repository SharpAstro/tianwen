# Probe BB (docs/plans/denoiser-training.md, "The pool is 100 percent OSC narrowband"): does the SHIPPED
# denoiser (e2_wide_s2, trained on 17 narrowband sessions) already denoise broadband fields, or is
# broadband the pool's gap? No training: two measurements that decide whether a broadband arm is worth
# its seeds.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-25.
#
# THE FINDING THIS TESTS. Every arm so far trained on narrowband (L-Ultimate 3 nm, L-Quad): the plan's
#   fact 2 says the deployment target is broadband and the pool had none. 2026-09-25-full is the first
#   bake with broadband colour sessions: 53 of its 121 (IDAS LPS-D3, UV/IR cut, Baader, unnamed
#   broadband), 11 of them held out by the bake's own test split. Only 4 of those 11 have half-masters
#   (a drizzled session needs 120 subs for halves), so the eval is arms/bb-eval-4.txt: 4 fields, 3 rigs.
#
# MEASUREMENTS.
#   1. condpool.py --bake 2026-09-25-full --parts b: the conditioning plane of every colour session,
#      broadband flagged, against the range of the E9 WIDE pool the shipped model trained on.
#   2. n2n_starsplit.py --per-session on the new cache n2n-bb-eval4 (60 cells per field) AND on
#      n2n-e2-eval4b (the narrowband reference, 4 fields), same three models in the same run:
#      wide_s2 (shipped), warped_s2 (E2, trained off the low plane), v19d_s2 (the model before).
#
# DECISION RULE (what step 2, the broadband arm, needs to be worth running):
#   RUN the arm if wide_s2, at full strength, removes under HALF its own eval4b median on at least 2 of
#   the 4 broadband fields, or ADDS noise on any (the off-distribution signature arm X found), or at
#   matched removal (4 and 10 percent) costs clearly more on the stars / compact / extended columns than
#   it does on eval4b. Size its seeds from the gap measured here against the known seed sd (0.58 to 1.40
#   points), not from a default of three.
#   DO NOT run it if wide_s2's broadband removal sits inside its eval4b per-session range and the
#   matched-removal columns are no worse: broadband is then not the shipped model's gap, and that is
#   reported as the result.
#   Confidence: low either way. The plane over 76 sessions said the POOL's plane coverage was the lever;
#   a broadband field can sit inside that range and still differ in what the plane cannot see (star
#   density, less nebulosity, a different noise colour).
#
# WHAT IT CANNOT SETTLE. Four fields on three rigs against eval4b's four L-Ultimate fields on the ASI533
#   and SV605CC: a difference is filter, rig and field at once. It is reported as a finding, not a claim,
#   and a broadband arm's own eval has to rotate the rig axis before its result is.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED (it outlives any Claude shell), and read the status file, never the log:
#   $s = (Resolve-Path .\run-bb-probe.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# The heartbeat finds it by C:\temp\e2\bb-probe.pid (this process's id) and bb-probe.status.
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'bb-probe.status'
$log = Join-Path $LogDir 'bb-probe.log'
$PID | Out-File (Join-Path $LogDir 'bb-probe.pid') -Encoding ascii
"running $(Get-Date -Format o): starting" | Out-File $status -Encoding utf8

function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    # gaia_starmask plate-solves each session's master through the CLI; its default is another checkout.
    $env:TIANWEN_CLI = (Resolve-Path $Tianwen).Path
    $head = (git -C $PSScriptRoot rev-parse --short HEAD)
    "probe bb at $head, CLI $env:TIANWEN_CLI built $((Get-Item $env:TIANWEN_CLI).LastWriteTime.ToString('o'))" | Tee-Object -FilePath $log -Append

    $snap = Join-Path $LogDir 'scripts-bb-probe'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-bb-probe.ps1 $snap -Force
    Copy-Item arms\bb-eval-4.txt, arms\bb-eval-train-2.txt, arms\wide-train-17.txt $snap -Force

    # 1. The conditioning plane over the new bake.
    Set-Status 'condpool'
    & python condpool.py --bake $Bake --parts b *> (Join-Path $LogDir 'bb-condpool.txt')
    if ($LASTEXITCODE -ne 0) { throw "condpool failed (exit $LASTEXITCODE)" }

    # 2. The broadband eval cache, prepared once and never edited.
    $cache = Join-Path $Scratch 'n2n-bb-eval4'
    if (Test-Path (Join-Path $cache 'meta.json')) {
        "prepare : $cache present, reused" | Tee-Object -FilePath $log -Append
    }
    else {
        Set-Status 'prepare n2n-bb-eval4'
        "prepare -> $cache" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare --root $Bake --cache $cache `
            --train-from-list arms\bb-eval-train-2.txt --val-from-list arms\bb-eval-4.txt `
            --cells-per-session 5 --val-cells-per-session 60 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    }

    # 3. The three models on the broadband eval and on the narrowband reference, per session.
    $models = @(
        "wide_s2=$Scratch\n2n-e2-wide\e2_wide_s2.pt",
        "warped_s2=$Scratch\n2n-e2-warped\e2_warped_s2.pt",
        "v19d_s2=$Scratch\n2n-d8\n2n_v19d_s2.pt")
    foreach ($m in $models) { $p = $m.Split('=', 2)[1]; if (-not (Test-Path $p)) { throw "no checkpoint $p" } }
    # Each cache's stars come from ITS bake's masters: the broadband cache from this bake, solved into a
    # folder of its own; eval4b from the bakes it was prepared from (gaia_starmask's defaults, unchanged).
    $bakeFor = @{ 'n2n-bb-eval4' = $Bake; 'n2n-e2-eval4b' = $null }
    foreach ($c in @('n2n-bb-eval4', 'n2n-e2-eval4b')) {
        if ($bakeFor[$c]) {
            $env:TIANWEN_BAKES = $bakeFor[$c]
            $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
        }
        else {
            Remove-Item Env:TIANWEN_BAKES, Env:TIANWEN_SOLVED_MASTERS -ErrorAction SilentlyContinue
        }
        Set-Status "starsplit $c"
        "starsplit $c (bakes: $($env:TIANWEN_BAKES ?? 'defaults'))" | Tee-Object -FilePath $log -Append
        & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --per-session `
            *> (Join-Path $LogDir "bb-starsplit-$c.txt")
        if ($LASTEXITCODE -ne 0) { throw "starsplit failed on $c (exit $LASTEXITCODE)" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
