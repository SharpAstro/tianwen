# P2 E1d: the difference width by Moffat COMPOSITION (est-c) beside quadrature (est-w) and exact.
# Pre-registered in docs/plans/deconvolver-training.md, "E1d, pre-registered 2026-09-07": est-c's
# estW/true at 1.3-1.6x within 0.95 to 1.05 (from 1.24), arm i's fabrication gone; killed if it stays
# above 1.10. Same six masters, seeds and 60 iterations as E1b. Release build made 11:10-11:11 from
# 9fc903a0's tree with -p:BuildProjectReferences=false (the CLI's Release output is locked by E2.9).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'oracle-e1d.status'
$log = Join-Path $LogDir 'oracle-e1d.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '60'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated,estimated-composed'
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release" | Out-File $log -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" `
        --output Detailed *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
