# P2 E1c: WHICH check makes PsfProfileFit refuse, over E1b's 180 rows. Pre-registered in
# docs/plans/deconvolver-training.md, "E1c, pre-registered 2026-09-07". One RL iteration: the recovery
# columns are not read, the refusal tally at the end is.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'oracle-e1c.status'
$log = Join-Path $LogDir 'oracle-e1c.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_PSF_STORE_DIR = $Store
$env:TIANWEN_ORACLE_MASTERS = '6'
$env:TIANWEN_ORACLE_ITERS = '1'
$env:TIANWEN_ORACLE_KERNEL = 'exact,estimated'
try {
    "commit $(git rev-parse --short HEAD) (dirty probe: $((git status --porcelain -- src/TianWen.Lib.Tests/DeconvolutionOracleCeilingProbe.cs src/TianWen.Lib/Imaging/Dataset/PsfProfileFit.cs) -ne $null))" | Out-File $log -Encoding utf8
    # DEBUG, deliberately: the Release rebuild is blocked while E2.9's tianwen.exe holds the CLI's
    # Release output (the test project references the CLI project), and a refusal tally does not
    # depend on the optimiser. The Debug build was made from this tree minutes before launch.
    "configuration Debug (Release output locked by the running E2.9 pass)" | Out-File $log -Append -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Debug --no-build `
        --filter "FullyQualifiedName~ReportHowMuchOfAKnownBlurAnOracleRecovers" `
        --logger "console;verbosity=detailed" *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
