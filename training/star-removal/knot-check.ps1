# #2276's check on real plates (2026-11-20): the two sessions of r4f-check.ps1 exported from the STORED plates with the fixed
# build (the build at "The stars export draws no knot at an angle that is not a number ..."), R4d's line and
# --noise-as-recorded, so that it differs from r4f-check's stored export (bh-bin8) by the build alone.
# As run on DE1 (the three were written into the script then; they are arguments since it came into the tree, 2026-11-21):
#   pwsh -File knot-check.ps1 -Bin D:/temp/tianwen-scratch/bh-bin11/tianwen.exe -Store D:/Astro-Dataset/2026-10-12-full `
#       -Out D:/temp/tianwen-scratch/knot-check
param(
    # The export tool, the fixed build (bh-bin11 on DE1).
    [Parameter(Mandatory)] [string]$Bin,
    # The dataset store. Read, never written; its starless root is the stored plates.
    [Parameter(Mandatory)] [string]$Store,
    # Where the export goes (<Out>/fixed).
    [Parameter(Mandatory)] [string]$Out
)
$ErrorActionPreference = 'Stop'
$sessions = @(
    'ZWO-ASI585MC-Pro/Unidentified-Broadband/Tarantula-Nebula/2024-10-02|ZWO ASI585MC Pro|Tarantula Neb  ZS61 ASI585 0x8 120s 7o 252g -10deg 66rb|Unidentified-Broadband',
    'ZWO-ASI585MC-Pro/Unidentified-Broadband-BlueCut/Helix-Nebula/2025-08-09|ZWO ASI585MC Pro|Helix Nebula|Unidentified-Broadband-BlueCut')
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not (Test-Path "$Out/fixed.done")) {
    $a = @('dataset', 'degrade', '--bake', "`"$Store`"", '--plates', "`"$Store/starless`"", '--mode', 'stars', '--shape', 'warped',
        '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1',
        '--background', 'synthetic', '--steer', '0.5,2,0.8,1', '--nebula-knots', '--bright-knots-wide', '--stretch-from', 'master',
        '--star-beta-draw', '0.3,1.3,6,3', '--faint-stars', '3,1', '--saturated-own-share', '--saturated-measured', '--halo-over-plate',
        '--noise-as-recorded', '--out', "`"$Out/fixed`"")
    foreach ($s in $sessions) { $a += '--session'; $a += "`"$s`"" }
    $p = Start-Process -FilePath $Bin -ArgumentList $a -RedirectStandardOutput "$Out/fixed.log" -RedirectStandardError "$Out/fixed.err.log" -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    "fixed exit $($p.ExitCode)"
    if ($p.ExitCode -eq 0) { 'done' | Out-File "$Out/fixed.done" }
}
