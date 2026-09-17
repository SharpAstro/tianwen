# Validation of the registration refiner's unmoved rule (docs/plans/deconvolver-training.md, "The third
# finding placed"), pre-registered there before this ran. Three steps in one detached job:
#   1. the whole Orion 2025-10-15 night again with the fix, Float16Staged, the reference pinned to frame
#      0033 (the frame the earlier run chose), into exp-full-fixed;
#   2. its manifest filtered to the same six frames the near6 runs used (0030 0032 0033 0036 0037 0040);
#   3. those six stacked in RAM with --save-normalized into exp-near6-fixed, so the scatter probe reads
#      the warped frames (TIANWEN_E210_NORM_EXP=exp-near6-fixed).
# Prediction: 300 to 700 "unmoved dropped" per frame in the register log, refine rms under 0.6 px; the six
# warped frames' median offsets against the reference under 0.15 px; near6 master green width under 2.3
# px (was 2.65), whole night under 2.4 (was 2.73). Kill: any frame still off by over 0.5 px, or the
# whole-night master not under 2.6 px.
# Release CLI rebuilt from the working tree with the fix (RegistrationRefiner.UnmovedTolerancePx).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-orion-fixed.status'
$log = Join-Path $LogDir 'e210-orion-fixed.log'
"running full $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
$full = Join-Path $Out 'exp-full-fixed'
$near = Join-Path $Out 'exp-near6-fixed'
try {
    foreach ($d in @($full, $near)) {
        New-Item -ItemType Directory -Force $d | Out-Null
        if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $d -Recurse -Force }
    }
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    Start-Sleep -Seconds 2

    # 1. the whole night, reference pinned
    & $exe stack $Archive -o $full --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy Float16Staged `
        --reference-frame 02-35-51__13.10_120.00s_0033 --no-plate-solve --output-format none *>> $log
    if ($LASTEXITCODE -ne 0) { throw "full stack exited $LASTEXITCODE" }
    "running split $(Get-Date -Format o)" | Out-File $status -Encoding utf8

    # 2. the same six frames out of the new manifest
    $fullManifest = Get-ChildItem (Join-Path $full 'master_*.manifest.json') | Select-Object -First 1
    if (-not $fullManifest) { throw 'the full run wrote no manifest' }
    $nearManifest = Join-Path $full 'master_GreatOrionNebula_light_120s_12C_g120-near6-fixed.manifest.json'
    $py = @"
import json, os, sys
src, dst = sys.argv[1], sys.argv[2]
keep = ('_0030.fits', '_0032.fits', '_0033.fits', '_0036.fits', '_0037.fits', '_0040.fits')
m = json.load(open(src, encoding='utf-8'))
m['Frames'] = [f for f in m['Frames'] if os.path.basename(f['Path']).endswith(keep)]
json.dump(m, open(dst, 'w', encoding='utf-8'), indent=1)
print(len(m['Frames']), 'frames kept; reference', os.path.basename(m['ReferencePath']))
"@
    $pyFile = Join-Path $LogDir 'e210-near6-fixed-split.py'
    $py | Out-File $pyFile -Encoding utf8
    python $pyFile $fullManifest.FullName $nearManifest *>> $log
    if ($LASTEXITCODE -ne 0) { throw "manifest split exited $LASTEXITCODE" }
    "running near6 $(Get-Date -Format o)" | Out-File $status -Encoding utf8

    # 3. the six in RAM, warped frames saved
    & $exe stack $Archive -o $near --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy InRamAllFrames `
        --no-plate-solve --output-format none --manifest $nearManifest --save-normalized *>> $log
    if ($LASTEXITCODE -ne 0) { throw "near6 stack exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
