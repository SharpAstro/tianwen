# E2.10a, second run: both masters in the unit domain, the crop chosen by star count (the first run's
# centred crop sat on M42's core with 17 stars and both fits refused). CPU arm then SAS arm, Release
# test build from the commit after 971f0756 that carries the probe fixes. Pre-registration unchanged
# (deconvolver-training.md, E2.10a).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Pair = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210a-pair.status'
Set-Location (Join-Path $Repo 'src')
$commit = "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Release"
try {
    "running cpu $(Get-Date -Format o)" | Out-File $status -Encoding utf8
    $env:TIANWEN_E210_PAIR_DIR = $Pair
    $env:TIANWEN_E210_SAS = '0'
    $commit | Out-File (Join-Path $LogDir 'e210a-pair-cpu.txt') -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~SeeingSplitPairProbe.ReportWhatTheOracleRecoversOnARealSeeingSplit" `
        --output Detailed *>> (Join-Path $LogDir 'e210a-pair-cpu.txt')
    if ($LASTEXITCODE -ne 0) { throw "pair probe (cpu) exited $LASTEXITCODE" }

    "running sas $(Get-Date -Format o)" | Out-File $status -Encoding utf8
    $env:TIANWEN_E210_SAS = '1'
    $commit | Out-File (Join-Path $LogDir 'e210a-pair-sas.txt') -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~SeeingSplitPairProbe.ReportWhatTheShippedGraphDoesOnARealSeeingSplit" `
        --output Detailed *>> (Join-Path $LogDir 'e210a-pair-sas.txt')
    if ($LASTEXITCODE -ne 0) { throw "pair probe (sas) exited $LASTEXITCODE" }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
