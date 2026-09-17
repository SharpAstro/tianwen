# R1's real half (docs/plans/deconvolver-training.md, "R1: the warp kernel", pre-registered): the six
# near frames and the whole Orion 2025-10-15 night stacked from the FIXED manifests under Lanczos-3, in
# RAM with the warped frames saved, so the per-star probe (TIANWEN_E210_NORM_EXP=exp-near6-lanczos /
# exp-full-lanczos) and the stage probe (masters' green fit) read them against the bilinear twins
# (exp-near6-fixed, exp-full-norm). Debug CLI: the Release one is held by the store re-measure.
# Prediction: fractional-phase frames within 5 percent of the integer-phase ones (2.14 to 2.28 rather
# than 2.38 to 2.69); near6 master per-star under 2.25 and fit under 2.30 (2.39 / 2.47 bilinear); whole
# night fit under 2.5 (2.81); ringing under 10 percent. Kill: fractional frames above 2.4, or ringing
# over 20 percent.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-r1-lanczos.status'
$log = Join-Path $LogDir 'e210-r1-lanczos.log'
"running near6 $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Debug\net10.0\tianwen.exe'
$nearManifest = Join-Path $Out 'exp-full-fixed\master_GreatOrionNebula_light_120s_12C_g120-near6-fixed.manifest.json'
$fullManifest = Get-ChildItem (Join-Path $Out 'exp-full-fixed\master_*.manifest.json') | Where-Object { $_.Name -notmatch 'near6' } | Select-Object -First 1
try {
    if (-not (Test-Path $nearManifest)) { throw "no near6 manifest at $nearManifest" }
    if (-not $fullManifest) { throw 'no full manifest under exp-full-fixed' }
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    Start-Sleep -Seconds 2
    foreach ($run in @(@{ Dir = 'exp-near6-lanczos'; Manifest = $nearManifest }, @{ Dir = 'exp-full-lanczos'; Manifest = $fullManifest.FullName })) {
        $dir = Join-Path $Out $run.Dir
        New-Item -ItemType Directory -Force $dir | Out-Null
        if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force }
        "running $($run.Dir) $(Get-Date -Format o)" | Out-File $status -Encoding utf8
        & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy InRamAllFrames `
            --warp-interpolation Lanczos3 --no-plate-solve --output-format none --manifest $run.Manifest --save-normalized *>> $log
        if ($LASTEXITCODE -ne 0) { throw "$($run.Dir) exited $LASTEXITCODE" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
