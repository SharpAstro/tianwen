# E16's eval caches: the three ring-fixed eval caches of E14 (run-poolrf-evalrf.ps1, step 5) prepared again, same
# bake, same lists and cell counts, with the per-pixel noise planes `tianwen dataset noise-planes` wrote for their
# master and half tiles (the calibration ESTIMATED from each retained master, as a runner would). New cache names
# (-rfs), so the -rf caches every earlier model was scored on stay exactly as they are; the tiles are the same
# tiles, which the script proves by comparing the cell keys and one slot's bytes against the -rf cache.
#
#   pwsh -NoProfile -File .\run-evalrf-planes.ps1 [-Planes C:\temp\tianwen-scratch\noise-planes\2026-09-25-full]
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Planes = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'noise-planes\2026-09-25-full'),
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch')
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
foreach ($c in @(@{ Name = 'n2n-bb-eval4'; Train = 'arms\bb-eval-train-2.txt'; Val = 'arms\bb-eval-4.txt'; Cells = 60 },
                 @{ Name = 'n2n-e2-eval4b'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4b-25full.txt'; Cells = 60 },
                 @{ Name = 'n2n-eval4'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4-25full.txt'; Cells = 48 })) {
    $cache = Join-Path $Scratch "$($c.Name)-rfs"
    if (Test-Path (Join-Path $cache 'meta.json')) { "prepare $($c.Name)-rfs: present, skipped"; continue }
    & python n2n_smoke.py --prepare --root $Bake --sigma-root $Planes --cache $cache --train-from-list $c.Train `
        --val-from-list $c.Val --cells-per-session 5 --val-cells-per-session $c.Cells
    if ($LASTEXITCODE -ne 0) { throw "prepare $($c.Name)-rfs failed (exit $LASTEXITCODE)" }
    $same = & python -c "import sys, numpy as np, n2n_smoke as S; a, ma = S.open_cache(sys.argv[1]); b, mb = S.open_cache(sys.argv[2]); print(ma['keys'] == mb['keys'] and bool(np.array_equal(a[:, S.SLOT_HALF_A], b[:, S.SLOT_HALF_A])))" `
        (Join-Path $Scratch "$($c.Name)-rf") $cache
    if ("$same".Trim() -ne 'True') { throw "$($c.Name)-rfs does not hold $($c.Name)-rf's cells and tiles ($same)" }
    "prepared $($c.Name)-rfs: the same cells and half-A tiles as $($c.Name)-rf"
}
