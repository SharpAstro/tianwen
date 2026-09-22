# P2 E1g-2: PsfProfileFit fits the Moffat over the CORE only (bins above 2 percent of the peak, about two
# FWHM) and reports the far wing beside it, over E1e's 180 rows. Pre-registered in
# docs/plans/deconvolver-training.md, "E1g-2": refusals under 10 of 180 (E1e: 30), est-c's width ratios
# unchanged within 0.02 on the rows both fit, the reported betas higher on the sharper masters; killed if
# widths move by more than 0.05 on the clean rows or the sharp masters still refuse. Same six masters,
# seeds, 60 iterations, arms exact and estimated-composed, band signal, as E1e (C:/temp/e2/oracle-e1e.txt
# is the "before"; oracle-e1g.txt is the withdrawn relative floor). Release Lib and tests rebuilt from the
# working tree with E1g-2 (tests with -p:BuildProjectReferences=false while the CLI's Release output is
# held by the store re-measure).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'oracle-e1g2.status'
$log = Join-Path $LogDir 'oracle-e1g2.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '60'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated-composed'
$env:TIANWEN_ORACLE_BAND = 'signal'
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release; band signal; E1g-2 core fit" | Out-File $log -Encoding utf8
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
