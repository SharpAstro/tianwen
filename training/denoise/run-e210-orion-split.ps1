# P2 E2.10 steps 3 and 4 for the Great Orion Nebula 2025-10-15 session: wait for the whole-session
# stack (run-e210-orion-full.ps1) to write master_<slug>.manifest.json, split it into the sharpest and
# softest thirds on the store's SubFwhm (tools/psf-seeing-split.py, reference kept in both), then stack
# each third with --manifest into its OWN output dir (the slug, and so the master's file name, is the
# group's and would otherwise overwrite the full master), seeding each with the full run's calibration
# master cache so the bias/dark/flat masters are not rebuilt. Same strategy as the full run.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$Session = 'Great-Orion-Nebula/2025-10-15',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-orion-split.status'
$log = Join-Path $LogDir 'e210-orion-split.log'
"waiting $(Get-Date -Format o) for e210-orion-full.status" | Out-File $status -Encoding utf8
$gate = Join-Path $LogDir 'e210-orion-full.status'
while (-not (Test-Path $gate) -or -not ((Get-Content $gate -Raw) -match '^(done|failed)')) {
    Start-Sleep -Seconds 30
}
if ((Get-Content $gate -Raw) -match '^failed') {
    "failed $(Get-Date -Format o): the whole-session stack failed, nothing to split" | Out-File $status -Encoding utf8
    exit 1
}
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
Set-Location $Repo
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
try {
    $manifests = @(Get-ChildItem (Join-Path $Out 'master_*.manifest.json') | Where-Object { $_.Name -notmatch '-(sharp|soft)\.manifest\.json$' })
    if ($manifests.Count -ne 1) { throw "expected one full-session manifest under $Out, found $($manifests.Count)" }
    $manifest = $manifests[0].FullName
    "manifest $manifest" | Out-File $log -Encoding utf8
    & python tools/psf-seeing-split.py $Store --session $Session --manifest $manifest *>> $log
    if ($LASTEXITCODE -ne 0) { throw "psf-seeing-split exited $LASTEXITCODE" }
    $stem = $manifests[0].Name -replace '\.manifest\.json$', ''
    foreach ($label in @('sharp', 'soft')) {
        $third = Join-Path $Out "$stem-$label.manifest.json"
        if (-not (Test-Path $third)) { throw "split did not write $third" }
        $dir = Join-Path $Out $label
        New-Item -ItemType Directory -Force $dir | Out-Null
        if (Test-Path (Join-Path $Out 'masters')) {
            Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force
        }
        "=== $label -> $dir" | Out-File $log -Append -Encoding utf8
        & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy Float16Staged `
            --no-plate-solve --output-format none --manifest $third *>> $log
        if ($LASTEXITCODE -ne 0) { throw "tianwen stack ($label) exited $LASTEXITCODE" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
