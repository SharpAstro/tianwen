# P2 E1e: the estimator's stack selected by an absolute SIGNAL FLOOR (PsfProfileFit.StarSelection.SignalFloor,
# TIANWEN_ORACLE_BAND=signal) instead of the 55th to 75th percentile brightness band, over E1b's 180 rows.
# Pre-registered in docs/plans/deconvolver-training.md, "E1e, pre-registered 2026-09-07": refused rows fall
# from 75 to under 30 of 180 (the four rich clean channels fit, most of the 20 noisy observed PoorFit refusals
# fit with them, the two TooFewStacked stay); where both selections fit, est-c's width ratio is unchanged
# within 0.05. Killed if refusals stay above 60 of 180. Same six masters, seeds and 60 iterations as E1b,
# arms exact and estimated-composed. Release build made 11:45 from 6a031fd9's clean tree, full project
# graph (E2.9's tianwen.exe had exited, so nothing was locked).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'oracle-e1e.status'
$log = Join-Path $LogDir 'oracle-e1e.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '60'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated-composed'
$env:TIANWEN_ORACLE_BAND = 'signal'
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release; band signal" | Out-File $log -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" `
        --logger "console;verbosity=detailed" *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
