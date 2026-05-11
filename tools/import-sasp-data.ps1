<#
.SYNOPSIS
Import SASP_data.fits from setiastro/setiastrosuitepro and convert to .gs.gz
catalog files for TianWen consumption.

.DESCRIPTION
Downloads SASP_data.fits from the setiastro GitHub repo (or reads a local
copy) and extracts all SENSOR (QE), FILTER (transmission), and SED (Pickles
stellar spectra) HDUs into three .gs.gz files:

  pickles_sed.gs.gz    - Pickles stellar spectral energy distributions
  sensor_qe.gs.gz      - Camera sensor quantum efficiency curves
  filter_curves.gs.gz  - Filter transmission curves

Output is written to src/TianWen.Lib/Astrometry/Catalogs/ alongside the
existing SIMBAD catalog .gs.gz files.

.EXAMPLE
pwsh -NoProfile -File tools/import-sasp-data.ps1
#>
[CmdletBinding()]
param(
    [string] $SaspFits,
    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

$projDir = Split-Path -Parent $PSCommandPath
$projArg = "--project", (Join-Path $projDir "import-sasp-data" "")

$dotnetArgs = @('run') + $projArg
if ($SaspFits)    { $dotnetArgs += '--sasp-fits', $SaspFits }
if ($OutputDir)   { $dotnetArgs += '--output-dir', $OutputDir }

& dotnet $dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet run failed (exit $LASTEXITCODE)" }
