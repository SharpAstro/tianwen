# E3.3 (task 33): staged against drizzle, fairly, on one night.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's re-bake step 2 ("drizzle's kernel
# cost") and its caveat: every per-star reading so far was taken against VNG-DEBAYERED warped frames,
# so the staged path's "no kernel cost" sat on top of the debayer's own interpolation, which drizzle
# never performs. The fair reference is the sub's width on its OWN mosaic: the store's green-plane
# width (`SubFwhmGreen`, a VNG-plane profile fit at the detector's positions on each sub), and the
# comparison is each strategy's MASTER measured by the store's own master statistic
# (`FindStarsAsync` at snr 5 over 3000 stars, then `PsfProfileFit.Measure` per channel), so both
# sides are numbers the store already speaks.
#
# THE NIGHT. SV605CC L-Quad Great Orion Nebula 2025-10-15 (68 subs in the store, 71 in the manifest;
#   the archive's best real-blur pair and its widest within-night spread, green p10 / p50 / p90 =
#   1.82 / 2.16 / 2.46 px). Its store master is drizzle at pixfrac 1: green profile 2.514 px.
#   Registration is SHARED across the arms through the post-fix full manifest (2026-09-07 08:49, 71
#   matched), so the arms differ in the integration path alone.
#
# THE ARMS. (a) Float16Staged with the clamped Lanczos-3 warp (the store's staged path);
#   (b) BayerDrizzle pixfrac 1.0 (the store's OSC path); (c) pixfrac 0.7; (d) pixfrac 0.5.
#
# PREDICTION. Against the sub reference 2.16 px on green: drizzle at pixfrac 1 sits at about
#   sqrt(2.16^2 + 0.65^2) = 2.26 plus whatever a mean over a 1.36x within-night spread adds (the
#   store's 2.514 says that addition is real); 0.7 and 0.5 shrink the kernel term to 0.48 and
#   0.34 px; the staged Lanczos master carries no kernel term but the VNG interpolation's own
#   widening, which no reading has yet separated from the seeing spread. The arithmetic says the
#   ORDER is staged < 0.5 < 0.7 < 1.0 if VNG's widening is under 0.34 px in quadrature, and the
#   question this run answers is whether it is.
# PRIMARY READOUT: the probe's green-channel `fwhm px` per arm (MasterProfileFitProbe,
#   TIANWEN_E33_MASTERS), the store master's own line reproducing 2.514 as the probe's self-check.
# DECISION IT FEEDS (the user's): whether the 52 OSC sessions move off drizzle, and at what pixfrac.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log while it runs:
#   $s = "$PWD\run-e33-staged-vs-drizzle.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e33',
    [string]$LogDir = 'C:\temp\e2',
    [string]$Manifest = 'C:\temp\e2\e210-orion\kept-manifests\exp-full-lanczos\master_GreatOrionNebula_light_120s_12C_g120.manifest.json',
    [string]$CalibrationMasters = 'C:\temp\e2\e210-orion\masters'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e33.status'
$log = Join-Path $LogDir 'e33.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
if (-not (Test-Path $Manifest)) { throw "no manifest at $Manifest" }

$arms = @(
    @{ Name = 'staged-lanczos'; Args = @('--strategy', 'Float16Staged', '--warp-interpolation', 'Lanczos3Clamped') },
    @{ Name = 'drizzle-p10';    Args = @('--strategy', 'BayerDrizzle', '--drizzle-pixfrac', '1.0', '--drizzle-min-frames', '6') },
    @{ Name = 'drizzle-p07';    Args = @('--strategy', 'BayerDrizzle', '--drizzle-pixfrac', '0.7', '--drizzle-min-frames', '6') },
    @{ Name = 'drizzle-p05';    Args = @('--strategy', 'BayerDrizzle', '--drizzle-pixfrac', '0.5', '--drizzle-min-frames', '6') }
)
try {
    "=== E3.3 staged vs drizzle: $(& $exe --version 2>&1 | Select-Object -First 1) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    foreach ($arm in $arms) {
        $dir = Join-Path $Out $arm.Name
        if (Get-ChildItem $dir -Filter 'master_*.fits' -ErrorAction SilentlyContinue) {
            "arm $($arm.Name): master present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        New-Item -ItemType Directory -Force $dir | Out-Null
        if (Test-Path $CalibrationMasters) { Copy-Item $CalibrationMasters $dir -Recurse -Force }
        "arm $($arm.Name) -> $dir $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 `
            --no-plate-solve --output-format none --manifest $Manifest @($arm.Args) *>> $log
        if ($LASTEXITCODE -ne 0) { throw "tianwen stack exited $LASTEXITCODE on arm $($arm.Name)" }
        "arm $($arm.Name) done $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
