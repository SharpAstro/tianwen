# P2 E2.10a, the CPU arm: SeeingSplitPairProbe.ReportWhatTheOracleRecoversOnARealSeeingSplit over the
# Orion sharp/soft thirds (C:/temp/e2/e210-orion/{sharp,soft}), from the DEBUG test build because D1's
# probe holds the Release binary and the GPU. Pre-registered in docs/plans/deconvolver-training.md,
# "E2.10a": est-c rec/A 1.00 to 1.10 on two of three channels at 60 iterations, stars over A 0.55 to
# 0.85, ring excess under 40 percent, est-cb within 0.05 of est-c; killed at rec/A over 1.20 on every
# channel with both fits present, or stars over A above 1.10 with rec/A under 1.00.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Pair = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210a-pair-cpu.status'
$log = Join-Path $LogDir 'e210a-pair-cpu.txt'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location (Join-Path $Repo 'src')
$env:TIANWEN_E210_PAIR_DIR = $Pair
try {
    "commit $(git rev-parse --short HEAD) (dirty: $((git status --porcelain -- src/) -ne $null)); configuration Debug" | Out-File $log -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Debug --no-build `
        --filter "FullyQualifiedName~SeeingSplitPairProbe.ReportWhatTheOracleRecoversOnARealSeeingSplit" `
        --output Detailed *>> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
