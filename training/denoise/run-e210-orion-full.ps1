# P2 E2.10, step 2 of tools/psf-seeing-split.py's recipe: stack the WHOLE Great Orion Nebula session
# (SVBONY SV605CC, Optolong L-Quad Enhance, 2025-10-15, 71 lights of which 60 survived the dataset's
# gate) once, so master_<slug>.manifest.json exists with every registered frame's transform and the
# reference the run chose. Step 3 splits that manifest into the sharpest and softest thirds on the
# store's SubFwhm; step 4 stacks each third with --manifest. Float16Staged on all three runs so A and B
# are integrated the same way as each other (a 20-frame drizzle would hole R and B); the deployed
# master was BayerDrizzle, which the plan notes as a 4 to 9 percent PSF difference to keep in mind.
# Release CLI built 13:01 from 0bb2aa2f (tianwen 7.1.0+0bb2aa2f).
# Second launch 13:35: the first run split the night into 49 / 18 / 4 frames by rounded sensor
# temperature (13.7 to 12.1 C) and went on to the other Orion sessions, because the light slug carries
# no filter. Now --group-temp-tolerance 2 (f2f05380) keeps the drift in one group, and the filter is the
# slug prefix only this night produces (the L-Ultimate SVBONY night is 120 s at 6 to 8 C, the ZWO night
# 60 s at -5 C). Rebuilt Release CLI from f2f05380.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-orion-full.status'
$log = Join-Path $LogDir 'e210-orion-full.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
try {
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    & $exe stack $Archive -o $Out --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy Float16Staged `
        --no-plate-solve --output-format none *>> $log
    if ($LASTEXITCODE -ne 0) { throw "tianwen stack exited $LASTEXITCODE" }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
