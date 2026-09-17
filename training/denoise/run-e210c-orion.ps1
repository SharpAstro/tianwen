# P2 E2.10c (docs/plans/deconvolver-training.md, "E2.10c, pre-registered 2026-09-07 late evening"): the
# seeing-split pair on the Orion L-Quad 2025-10-15 night, ranked on the GUARDED store's green fits
# (55 of 68 subs; thirds 1.89 against 2.38 px, 1.26x, the archive's best pair), stacked with Lanczos-3.
# E2.10a built this night's thirds on the floor-ranked list and found no pair (2.82 against 2.72).
#   1. the whole night, Float16Staged, --warp-interpolation Lanczos3, into $Out;
#   2. tools/psf-seeing-split.py --width green splits the manifest into the sharpest and softest thirds;
#   3. each third stacked from its manifest with the same options into $Out\sharp and $Out\soft;
#   4. the pair probe (SeeingSplitPairProbe, TIANWEN_E210_PAIR_DIR=$Out) runs separately after this.
# Prediction: the thirds' masters differ 1.15 to 1.26 on the green fit, both fits succeed; est-c rec/A
# 1.00 to 1.10 on two of three channels at 60 iterations, stars over A 0.7 to 0.95, ring under 40 percent.
# Kill: masters differ under 1.10 (the ranking was not what E2.10a lacked); or rec/A over 1.20
# everywhere; or stars over A above 1.10 with rec/A under 1.00 (fabrication).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$Out = 'C:\temp\e2\e210c-orion',
    [string]$Session = 'Great-Orion-Nebula/2025-10-15',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210c-orion.status'
$log = Join-Path $LogDir 'e210c-orion.log'
"running full $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
$common = @('--group-filter', 'GreatOrionNebula_light_120s_1', '--group-temp-tolerance', '2', '--strategy', 'Float16Staged',
            '--warp-interpolation', 'Lanczos3', '--no-plate-solve', '--output-format', 'none')
try {
    New-Item -ItemType Directory -Force $Out | Out-Null
    if (Test-Path 'C:\temp\e2\e210-orion\masters') { Copy-Item 'C:\temp\e2\e210-orion\masters' $Out -Recurse -Force }
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    Start-Sleep -Seconds 2

    & $exe stack $Archive -o $Out @common *>> $log
    if ($LASTEXITCODE -ne 0) { throw "full stack exited $LASTEXITCODE" }
    $manifests = @(Get-ChildItem (Join-Path $Out 'master_*.manifest.json') | Where-Object { $_.Name -notmatch '-(sharp|soft)\.manifest\.json$' })
    if ($manifests.Count -ne 1) { throw "expected one full-session manifest under $Out, found $($manifests.Count)" }
    $manifest = $manifests[0].FullName

    "running split $(Get-Date -Format o)" | Out-File $status -Encoding utf8
    Set-Location $Repo
    & python tools/psf-seeing-split.py $Store --session $Session --manifest $manifest --width green *>> $log
    if ($LASTEXITCODE -ne 0) { throw "psf-seeing-split exited $LASTEXITCODE" }
    $stem = $manifests[0].Name -replace '\.manifest\.json$', ''
    foreach ($label in @('sharp', 'soft')) {
        $third = Join-Path $Out "$stem-$label.manifest.json"
        if (-not (Test-Path $third)) { throw "split did not write $third" }
        $dir = Join-Path $Out $label
        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force
        "running $label $(Get-Date -Format o)" | Out-File $status -Encoding utf8
        & $exe stack $Archive -o $dir @common --manifest $third *>> $log
        if ($LASTEXITCODE -ne 0) { throw "$label stack exited $LASTEXITCODE" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
