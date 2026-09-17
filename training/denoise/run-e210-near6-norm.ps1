# The six frames nearest the reference, stacked once more with --save-normalized so each WARPED frame
# lands on disk on the shared canvas: star centroids frame by frame against the reference measure the
# registration scatter directly (InRamAllFrames: the one strategy that hands the frame writer to the integrator), which the width-versus-frame-count curve (2 to 3 frames at the subs'
# width, 6 or more at 2.6 to 2.8 px) says is about 0.7 px RMS.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-near6-norm.status'
$log = Join-Path $LogDir 'e210-near6-norm.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Debug\net10.0\tianwen.exe'
try {
    $manifest = Join-Path $Out 'master_GreatOrionNebula_light_120s_12C_g120-near6.manifest.json'
    $dir = Join-Path $Out 'exp-near6-norm'
    New-Item -ItemType Directory -Force $dir | Out-Null
    if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force }
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    Start-Sleep -Seconds 2
    & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy InRamAllFrames `
        --no-plate-solve --output-format none --manifest $manifest --save-normalized *>> $log
    if ($LASTEXITCODE -ne 0) { throw "tianwen stack exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
