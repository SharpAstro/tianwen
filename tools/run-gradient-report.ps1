<#
.SYNOPSIS
    Runs `tianwen dataset gradient-report` over the retained session masters of one or more dataset
    bakes, detached, writing the store and report into each bake's own stats/ beside psf-noise-report.md.

.DESCRIPTION
    The measurement behind gradient-remover-training.md G1 (and the place background-extraction.md's
    two reasoned thresholds get measured on real data). One master costs about nine fits of a 30 Mpx
    frame plus a plate solve, so a bake is a wall-clock job: it runs as its own process with a log and
    a status file, and this script returns at once. Nothing reads the log while the job appends to it;
    poll the status file instead. Re-running only measures masters not yet in the store (-Force
    re-measures them all; the store is append-only and the last record wins).

.PARAMETER Bakes
    Dataset bake roots. Each must have a session-masters/ folder; stats/ receives the output.

.PARAMETER Force
    Re-measure masters already in a bake's store.

.PARAMETER NoSolve
    Skip plate solving (no horizon or Moon direction in the frame).

.PARAMETER NoSweep
    Skip the threshold sweep (default fit only; eight fewer fits per master).

.PARAMETER Configuration
    Build configuration of the CLI to run. Release, because the Debug fit is four times slower.

.EXAMPLE
    tools/run-gradient-report.ps1 -Bakes D:\Astro-Dataset\2025-2026-darkscaled, D:\Astro-Dataset\2025-2026-organized
    Get-Content D:\Astro-Dataset\2025-2026-darkscaled\stats\gradient-report.status
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Bakes,
    [switch] $Force,
    [switch] $NoSolve,
    [switch] $NoSweep,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo "src/TianWen.Cli/bin/$Configuration/net10.0/tianwen.exe"
if (-not (Test-Path $cli)) {
    throw "CLI not built at $cli; run 'dotnet build src/TianWen.Cli -c $Configuration' first"
}

foreach ($bake in $Bakes) {
    $masters = Join-Path $bake 'session-masters'
    if (-not (Test-Path $masters)) {
        throw "$bake has no session-masters/ folder"
    }
}

$args = @('dataset', 'gradient-report')
$flags = @()
if ($Force) { $flags += '--force' }
if ($NoSolve) { $flags += '--no-solve' }
if ($NoSweep) { $flags += '--no-sweep' }

# One detached pwsh runs the bakes in sequence; each bake's status file says running / done / failed.
$script = @()
foreach ($bake in $Bakes) {
    $stats = Join-Path $bake 'stats'
    New-Item -ItemType Directory -Force $stats | Out-Null
    $status = Join-Path $stats 'gradient-report.status'
    $log = Join-Path $stats 'gradient-report.log'
    Set-Content -Path $status -Value 'running'
    $quoted = ($args + @('--masters', "`"$(Join-Path $bake 'session-masters')`"", '--out', "`"$bake`"") + $flags) -join ' '
    $script += "& `"$cli`" $quoted *> `"$log`""
    $script += "if (`$LASTEXITCODE -eq 0) { Set-Content -Path `"$status`" -Value 'done' } else { Set-Content -Path `"$status`" -Value `"failed exit `$LASTEXITCODE`" }"
}
$runner = Join-Path ([IO.Path]::GetTempPath()) "tianwen-gradient-report-$([IO.Path]::GetRandomFileName()).ps1"
Set-Content -Path $runner -Value ($script -join "`n")

$process = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', $runner) -PassThru -WindowStyle Hidden
Write-Host "gradient-report started, pid $($process.Id); runner $runner"
foreach ($bake in $Bakes) {
    Write-Host "  $bake -> $(Join-Path $bake 'stats/gradient-report.status')"
}
