# The whole Orion 2025-10-15 night (71 frames) stacked in RAM from the FIXED full manifest with
# --save-normalized, so every warped frame is on disk for the scatter probe
# (TIANWEN_E210_NORM_EXP=exp-full-norm). The pre-registered kill for the whole-night master was reached
# (2.81 px before and after the refiner fix), so the placement of every frame is measured next, not
# theorised about: a median offset per frame is a transform error, the rms about it is centroid scatter.
# 71 frames of 3 x 3195 x 3090 float32 are about 8 GB of RAM and of disk.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-full-norm.status'
$log = Join-Path $LogDir 'e210-full-norm.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
$dir = Join-Path $Out 'exp-full-norm'
try {
    New-Item -ItemType Directory -Force $dir | Out-Null
    if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force }
    $manifest = Get-ChildItem (Join-Path $Out 'exp-full-fixed\master_*.manifest.json') | Where-Object { $_.Name -notmatch 'near6' } | Select-Object -First 1
    if (-not $manifest) { throw 'no full manifest under exp-full-fixed' }
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    Start-Sleep -Seconds 2
    & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy InRamAllFrames `
        --no-plate-solve --output-format none --manifest $manifest.FullName --save-normalized *>> $log
    if ($LASTEXITCODE -ne 0) { throw "tianwen stack exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
