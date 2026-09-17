# P2 E2.10b (docs/plans/deconvolver-training.md, "E2.10b, pre-registered 2026-09-07 late evening"): the
# seeing-split pair again on a stack that keeps its inputs' width. Statue of Liberty Nebula 2026-02-14
# (SV605CC, L-Quad, 256 subs of 60 s at -5 C; green fits 1.40 / 1.60 / 1.70 px at p10 / p50 / p90).
# Second launch 20:25: the first stacked the night's thirteen 120 s frames (the pre-registration said
# 120 s; the 256 are 60 s) and the split refused a session key matching two records (the night's OBJECT
# card reads Skull and Crossbones Nebula on 45 frames); group and key corrected below.
#   1. the whole session, Float16Staged, --warp-interpolation Lanczos3, into $Out;
#   2. tools/psf-seeing-split.py --width green splits the manifest into the sharpest and softest thirds;
#   3. each third stacked from its manifest with the same options into $Out\sharp and $Out\soft;
#   4. the pair probe (SeeingSplitPairProbe, TIANWEN_E210_PAIR_DIR=$Out) runs separately after this.
# Prediction: the thirds' masters differ 1.10 to 1.16 on the green fit, both fits succeed; est-c rec/A
# 1.00 to 1.10 on two of three channels at 60 iterations, stars over A 0.7 to 0.95, ring under 40 percent.
# Kill: masters differ under 1.05; or rec/A over 1.20 everywhere; or stars over A above 1.10 with rec/A
# under 1.00. Release CLI rebuilt from the working tree (R1, the guard, the refiner rule, E1g-2).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Store = 'D:\Astro-Dataset\2026-09-full',
    [string]$Out = 'C:\temp\e2\e210b-statue',
    [string]$Session = 'Statue-of-Liberty-Nebula/2026-02-14|SVBONY SV605CC|Statue of Liberty Nebula',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210b-statue.status'
$log = Join-Path $LogDir 'e210b-statue.log'
"running full $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
$common = @('--group-filter', 'StatueOfLibertyNebula_light_60s', '--group-temp-tolerance', '2', '--strategy', 'Float16Staged',
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
