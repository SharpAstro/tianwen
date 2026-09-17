# P2 D1, the GPU half: PerChunkPsfOutputProbe over the seven retained Rim masters, the shipped SAS AI4
# graph run whole-image and per-tile on the same pixels, stars measured in three field-radius bins.
# Pre-registered in docs/plans/deconvolver-training.md, D1 "The GPU half, pre-registered": per-tile
# moves the inner/outer width ratio toward 1 where the input's is far from it, star counts within 10
# percent; killed by a bin narrower than the input's sharpest or a count moving over 20 percent.
# First run 12:42 from 6a031fd9 failed on the second master (the probe discarded the rewrapped unit-range
# image); Release rebuilt 13:06 from 0bb2aa2f's tree with the fix and the tiles-differing column, queued
# behind E2.8c by run-d1-after-e28c.ps1 so the card is not shared with a training run.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'd1-output-rim.status'
$log = Join-Path $LogDir 'd1-perchunk-output-rim.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_PSF_PROBE_FILTER = 'Rim'
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release" | Out-File $log -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~ReportWhetherPerTileConditioningChangesTheOutputWhereTheWidthDiffers" `
        --logger "console;verbosity=detailed" *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
