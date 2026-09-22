# After the Orion thirds are re-stacked with the CANVASX0/CANVASY0 cards (run-e210-orion-split.ps1,
# status e210-orion-split.status turning done): E2.10a's CPU arm (the estimator step and the RL oracle
# on soft against sharp), then its GPU arm (the shipped SAS graph on the same crop), then D1's psf01
# sensitivity check (the shipped graph at fixed psf01 0 / 0.5 / 1 on the 2025-05-02 Rim master, whose
# input c/o of 1.26 is the widest). All from the Release test build made from the commit that added the
# cards. Pre-registrations: E2.10a in docs/plans/deconvolver-training.md under E2.10; the sensitivity
# check is a readout of D1's null (three identical rows = the conditioning input is inert on this graph).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Pair = 'C:\temp\e2\e210-orion',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210a-d1s.status'
"waiting $(Get-Date -Format o) for e210-orion-split.status" | Out-File $status -Encoding utf8
$gate = Join-Path $LogDir 'e210-orion-split.status'
while (-not (Test-Path $gate) -or -not ((Get-Content $gate -Raw) -match '^(done|failed)')) {
    Start-Sleep -Seconds 20
}
if ((Get-Content $gate -Raw) -match '^failed') {
    "failed $(Get-Date -Format o): the re-stack failed" | Out-File $status -Encoding utf8
    exit 1
}
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

    "running d1 sensitivity $(Get-Date -Format o)" | Out-File $status -Encoding utf8
    $env:TIANWEN_PSF_STORE_DIR = $Store
    $env:TIANWEN_PSF_PROBE_FILTER = '2025-05-02'
    $env:TIANWEN_PSF_PROBE_SENSITIVITY = '1'
    $commit | Out-File (Join-Path $LogDir 'd1-psf01-sensitivity.txt') -Encoding utf8
    & dotnet test TianWen.Lib.Tests -c Release --no-build `
        --filter "FullyQualifiedName~PerChunkPsfOutputProbe.ReportWhetherTheShippedGraphRespondsToPsf01AtAll" `
        --output Detailed *>> (Join-Path $LogDir 'd1-psf01-sensitivity.txt')
    if ($LASTEXITCODE -ne 0) { throw "sensitivity probe exited $LASTEXITCODE" }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
