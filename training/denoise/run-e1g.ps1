# P2 E1g: PsfProfileFit's floor relative to the profile's own outer level (WingResidueFactor 3 over the
# median of the outermost quarter of bins), over E1e's 180 rows. Pre-registered in
# docs/plans/deconvolver-training.md, "E1g": refusals fall from 30 of 180 to under 15 with est-c's width
# ratios unchanged within 0.02 on the rows both fit; killed if widths move by more than 0.05 on the clean
# rows or refusals do not fall. Same six masters, seeds, 60 iterations, arms exact and estimated-composed,
# band signal, as E1e (C:/temp/e2/oracle-e1e.txt is the "before"). Release Lib and test binaries built
# from the working tree with E1g (the CLI's Release output is held by the store re-measure, so the test
# project was built with -p:BuildProjectReferences=false against the freshly built Release Lib).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'oracle-e1g.status'
$log = Join-Path $LogDir 'oracle-e1g.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '60'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated-composed'
$env:TIANWEN_ORACLE_BAND = 'signal'
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release; band signal; E1g floor" | Out-File $log -Encoding utf8
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
