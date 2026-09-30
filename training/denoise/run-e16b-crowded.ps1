# E16b's crowded-field check (docs/plans/denoiser-training.md, "E16b, a crowded-field check and the reading of a null"):
# waits for run-e16b.ps1 to finish its registered scoring, so the two never share the GPU, then scores every model on
# n2n-e16b-crowded in both plane conditions with e16b_crowded.py.
#
# Run DETACHED; read the status file, never the log. To stop it before it starts scoring, create C:\temp\e2\e16b-crowded.stop.
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',(Resolve-Path .\run-e16b-crowded.ps1).Path -WindowStyle Hidden
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-29-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tag = 'e16b-crowded'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$status = Join-Path $LogDir "$Tag.status"
$stopFile = Join-Path $LogDir "$Tag.stop"
$PID | Out-File (Join-Path $LogDir "$Tag.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Final([string]$dir, [string]$name) {
    $f = Join-Path $dir "${name}_final.pt"
    if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
}

try {
    Set-Status 'waiting for run-e16b.ps1 to finish scoring'
    while ($true) {
        if (Test-Path $stopFile) { "stopped $(Get-Date -Format o): before scoring" | Out-File $status -Encoding utf8; return }
        $e16b = (Get-Content (Join-Path $LogDir 'e16b.status') -Raw).Trim()
        if ($e16b -match '^(done|failed|stopped)') { break }
        Start-Sleep -Seconds 60
    }
    $models = @()
    foreach ($seed in 0..3) {
        $models += "convmap3_s$seed=$(Final (Join-Path $Scratch 'n2n-e16b-ctl') "bb_e16bctl_s$seed")"
        $models += "convmapb_s$seed=$(Final (Join-Path $Scratch 'n2n-e16b-arm') "bb_e16barm_s$seed")"
    }
    foreach ($seed in 0..3) { $models += "convmap_s$seed=$(Join-Path $Scratch "n2n-bb-ctl-rfm\bb_ctlmap_s$seed.pt")" }
    foreach ($seed in 0..3) { $models += "convrf_s$seed=$(Join-Path $Scratch "n2n-bb-ctl-rf\bb_ctlrf_s${seed}_final.pt")" }
    $models += "shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')"
    foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    $cache = Join-Path $Scratch 'n2n-e16b-crowded'
    foreach ($cond in @('orc', 'est')) {
        $out = Join-Path $LogDir "$Tag-$cond.txt"
        if (Test-Path $out) { continue }
        Set-Status "score $cond"
        $extra = @(if ($cond -eq 'orc') { '--plane-truth-anchor' })
        & python e16b_crowded.py --cache $cache --bake $Bake --models @models @extra *> "$out.partial"
        if ($LASTEXITCODE -ne 0) { throw "e16b_crowded.py $cond failed (exit $LASTEXITCODE); see $out.partial" }
        Move-Item "$out.partial" $out -Force
    }
    "done $(Get-Date -Format o): scored in both conditions" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
