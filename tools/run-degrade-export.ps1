<#
.SYNOPSIS
    Exports one or more degradation arms from a dataset bake, detached, with a status file per arm.

.DESCRIPTION
    The training-pair half of the model-training roadmap (denoiser E2, deconvolver E3). A full arm is
    51 sessions x N cells x (draws + 1) tiles and runs for hours, so it is a wall-clock job: its own
    process, a log, a status file, and this script returns at once. Nothing reads the log while the job
    appends to it; poll the status file instead. The export is resumable (a session already in
    degradations.jsonl is skipped), so a killed run continues where it stopped.

    **The arms share a seed on purpose.** Cell choice and the drawn noise LEVEL both derive from it, and
    only the noise SHAPE differs between white and warped, so the two arms differ in exactly the thing
    under test. Pass the same -Seed to every arm of one comparison.

.PARAMETER Bake
    The dataset bake to read: it must hold tiles-manifest.jsonl and session-masters/.

.PARAMETER OutRoot
    Parent directory for the arms; each lands in <OutRoot>/<arm name>.

.PARAMETER Arms
    Arm specs, "name:shape[:warpSigma]" (e.g. white, warped:0.5). Shape is white or warped.

.PARAMETER Cells
    Cells per session. The trainer's prepare picks its own subset from these, so export at least as many
    as the largest --val-cells-per-session a run will ask for.

.PARAMETER Draws
    Degraded draws per cell. Eight fills the trainer's sub slots.

.PARAMETER Mode
    noise (denoiser) or blur (deconvolver).

.EXAMPLE
    tools/run-degrade-export.ps1 -Bake D:\Astro-Dataset\2025-2026-organized -OutRoot D:\Astro-Dataset\degraded `
        -Arms white, warped:0.5 -Cells 120 -Draws 8
    Get-Content D:\Astro-Dataset\degraded\white\degrade.status
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Bake,
    [Parameter(Mandatory)] [string] $OutRoot,
    [Parameter(Mandatory)] [string[]] $Arms,
    [int] $Cells = 120,
    [int] $Draws = 8,
    [ValidateSet('noise', 'blur')] [string] $Mode = 'noise',
    [int] $Seed = 1,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo "src/TianWen.Cli/bin/$Configuration/net10.0/tianwen.exe"
if (-not (Test-Path $cli)) {
    throw "CLI not built at $cli; run 'dotnet build src/TianWen.Cli -c $Configuration' first"
}
foreach ($p in @((Join-Path $Bake 'tiles-manifest.jsonl'), (Join-Path $Bake 'session-masters'))) {
    if (-not (Test-Path $p)) { throw "$Bake is not a dataset bake: missing $p" }
}

$script = @()
$targets = @()
foreach ($arm in $Arms) {
    $parts = $arm.Split(':')
    $name = $parts[0]
    $shape = if ($parts.Count -gt 1) { $parts[1] } else { $parts[0] }
    $warp = if ($parts.Count -gt 2) { $parts[2] } else { '0' }
    if ($shape -notin @('white', 'warped')) { throw "arm '$arm': shape must be white or warped" }

    $out = Join-Path $OutRoot $name
    New-Item -ItemType Directory -Force $out | Out-Null
    $status = Join-Path $out 'degrade.status'
    $log = Join-Path $out 'degrade.log'
    Set-Content -Path $status -Value 'running'
    $targets += $status

    $cmd = "& `"$cli`" dataset degrade --bake `"$Bake`" --out `"$out`" --mode $Mode --shape $shape " +
           "--warp-sigma $warp --draws $Draws --cells $Cells --seed $Seed --measure-shape *> `"$log`""
    $script += $cmd
    $script += "if (`$LASTEXITCODE -eq 0) { Set-Content -Path `"$status`" -Value 'done' } else { Set-Content -Path `"$status`" -Value `"failed exit `$LASTEXITCODE`" }"
}

$runner = Join-Path ([IO.Path]::GetTempPath()) "tianwen-degrade-$([IO.Path]::GetRandomFileName()).ps1"
Set-Content -Path $runner -Value ($script -join "`n")
$process = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', $runner) -PassThru -WindowStyle Hidden
Write-Host "degrade export started, pid $($process.Id); runner $runner"
foreach ($t in $targets) { Write-Host "  $t" }
