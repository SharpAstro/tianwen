# R4f, before its registration (2026-11-20): what the grain-rule plates change in an export's tiles. Two sessions, a crowded
# one (the store's Tarantula of 2024-10-02) and a sparse one (Helix of 2025-08-09), exported with R4d's line (synth29's:
# bh-bin8, faint stars 3,1) once from the store's plates and once from the grain root. No arm is scored here.
# As run on DE1 (the four were written into the script then; they are arguments since it came into the tree, 2026-11-21):
#   pwsh -File r4f-check.ps1 -Bin D:/temp/tianwen-scratch/bh-bin8/tianwen.exe -Store D:/Astro-Dataset/2026-10-12-full `
#       -GrainPlates D:/temp/tianwen-scratch/starless-grain -Out D:/temp/tianwen-scratch/r4f-check
# The two exports are then read with tile_grain.py.
param(
    # The export tool, a frozen build of the line the arm exports with.
    [Parameter(Mandatory)] [string]$Bin,
    # The dataset store. Read, never written.
    [Parameter(Mandatory)] [string]$Store,
    # The root of the grain-rule plates (plate_grain.py's output, the store's side files beside them: grain_store.py).
    [Parameter(Mandatory)] [string]$GrainPlates,
    # Where the two exports go.
    [Parameter(Mandatory)] [string]$Out
)
$ErrorActionPreference = 'Stop'
$sessions = @(
    'ZWO-ASI585MC-Pro/Unidentified-Broadband/Tarantula-Nebula/2024-10-02|ZWO ASI585MC Pro|Tarantula Neb  ZS61 ASI585 0x8 120s 7o 252g -10deg 66rb|Unidentified-Broadband',
    'ZWO-ASI585MC-Pro/Unidentified-Broadband-BlueCut/Helix-Nebula/2025-08-09|ZWO ASI585MC Pro|Helix Nebula|Unidentified-Broadband-BlueCut')
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($side in @(@('stored', "$store/starless"), @('grain', $GrainPlates))) {
    $name, $plates = $side
    if (Test-Path "$out/$name.done") { continue }
    $a = @('dataset', 'degrade', '--bake', "`"$store`"", '--plates', "`"$plates`"", '--mode', 'stars', '--shape', 'warped',
        '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1',
        '--background', 'synthetic', '--steer', '0.5,2,0.8,1', '--nebula-knots', '--bright-knots-wide', '--stretch-from', 'master',
        '--star-beta-draw', '0.3,1.3,6,3', '--faint-stars', '3,1', '--saturated-own-share', '--saturated-measured', '--halo-over-plate',
        '--out', "`"$out/$name`"")
    foreach ($s in $sessions) { $a += '--session'; $a += "`"$s`"" }
    $p = Start-Process -FilePath $bin -ArgumentList $a -RedirectStandardOutput "$out/$name.log" -RedirectStandardError "$out/$name.err.log" -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    "$name exit $($p.ExitCode)"
    if ($p.ExitCode -eq 0) { 'done' | Out-File "$out/$name.done" }
}
