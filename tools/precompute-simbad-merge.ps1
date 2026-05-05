# Bake the simbad-merge snapshot embedded in TianWen.Lib.
#
# What this does:
#   1. Builds tools/precompute-simbad-merge/PrecomputeSimbadMerge.csproj (which transitively
#      builds TianWen.Lib so it can read the embedded catalog inputs).
#   2. Runs the tool with --output pointing at
#      src/TianWen.Lib/Astrometry/Catalogs/simbad_merge.bin.gz.
#   3. Re-builds TianWen.Lib so the next test/run picks up the freshly-baked snapshot.
#
# When to run this:
#   - Whenever any of the SIMBAD or NGC catalog inputs change (HR.gs.gz, GUM.gs.gz,
#     RCW.gs.gz, LDN.gs.gz, Dobashi.gs.gz, Sh.gs.gz, Barnard.gs.gz, Ced.gs.gz, CG.gs.gz,
#     vdB.gs.gz, DG.gs.gz, HH.gs.gz, Cl.gs.gz, NGC.gs.gz, NGC.addendum.gs.gz).
#   - When MergeSimbadRecords / UpdateObjectCommonNames / PopulateSimbadStarEntries change
#     (and AlgorithmVersion in SimbadMergeSnapshot.cs is bumped).
#   - At each release cut, to make sure the shipped snapshot is fresh.
#
# Safety net: if the embedded snapshot is stale, runtime falls back to the live SIMBAD merge
# loop with a per-phase timing entry (simbad-snapshot:stale) so it's visible in init telemetry.
# The tool is NOT required to run on every developer build.
#
# Usage:
#   pwsh tools/precompute-simbad-merge.ps1
#
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolProj = Join-Path $repoRoot 'tools/precompute-simbad-merge/PrecomputeSimbadMerge.csproj'
$libProj  = Join-Path $repoRoot 'src/TianWen.Lib/TianWen.Lib.csproj'
$outFile  = Join-Path $repoRoot 'src/TianWen.Lib/Astrometry/Catalogs/simbad_merge.bin.gz'

Write-Host "[1/3] Building precompute tool..." -ForegroundColor Cyan
& dotnet build $toolProj -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Tool build failed (exit $LASTEXITCODE)" }

Write-Host "[2/3] Running precompute tool -> $outFile" -ForegroundColor Cyan
& dotnet run --project $toolProj -c $Configuration --no-build -- --output $outFile
if ($LASTEXITCODE -ne 0) { throw "Precompute run failed (exit $LASTEXITCODE)" }

Write-Host "[3/3] Rebuilding TianWen.Lib so the new snapshot is embedded..." -ForegroundColor Cyan
& dotnet build $libProj -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Lib rebuild failed (exit $LASTEXITCODE)" }

$size = (Get-Item $outFile).Length
Write-Host ("Done. Snapshot {0:N0} bytes embedded into TianWen.Lib." -f $size) -ForegroundColor Green
