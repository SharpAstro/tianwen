# Task 25: the thirds' masters against their own subs, per star (E2.10b's Statue night).
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-13 from the plan's E2.10b reading and R1's method.
#
# WHY. E2.10b's sharp and soft thirds (79 and 80 of the Statue of Liberty 2026-02-14 night's 244 subs
#   of 60 s, Lanczos-3) fit 1.17 to 1.24x ABOVE their subs' store medians on the green profile fit.
#   Two statistics are in play (a VNG-plane fit at the detector's positions on a SUB against the fit
#   on the master's crop), so the ratio is not yet a finding. R1 read the same question on the Orion
#   near6 frames star by star: the warped frames a --save-normalized run leaves on the shared canvas,
#   each star paired with itself in the master, which removes the selection a deeper image makes.
#
# WHAT IT IS. Each third re-stacked from its kept manifest under InRamAllFrames with --save-normalized
#   (the warp is the clamped Lanczos-3 default of the Release binary), then
#   SeeingSplitDiagnosticProbe.ReportPerStarWidthsOfTheWarpedFramesAgainstTheirMaster on each stage
#   directory (TIANWEN_E210_PAIR_DIR=C:/temp/e2/e210b-statue, TIANWEN_E210_NORM_EXP=exp-sharp-norm /
#   exp-soft-norm): per frame, the master's FWHM over the frame's on the same stars.
#
# PREDICTION. As on Orion under the clamped kernel: the master is the MEAN of its warped frames to a
#   few tenths of a percent (master/frame p50 within 1.00 to 1.03 on the reference frame's row, the
#   others within the seeing spread), so the 1.17 to 1.24x is the two statistics and the mean over a
#   spread, not a stack defect. KILL of that reading: a master/frame ratio at or over 1.10 on the
#   reference frame's own row, which would say the integration itself widens.
# ---------------------------------------------------------------------------------------------------
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210b-statue',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e25.status'
$log = Join-Path $LogDir 'e25.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
try {
    "=== E2.5 Statue thirds with --save-normalized: $(& $exe --version 2>&1 | Select-Object -First 1) $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    foreach ($label in 'sharp', 'soft') {
        $manifest = Join-Path $Out "master_StatueofLibertyNebula_light_60s_-5C_g120-$label.manifest.json"
        if (-not (Test-Path $manifest)) { throw "no manifest at $manifest" }
        $dir = Join-Path $Out "exp-$label-norm"
        if (Get-ChildItem $dir -Filter 'master_*.fits' -ErrorAction SilentlyContinue) {
            "$label : master present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        New-Item -ItemType Directory -Force $dir | Out-Null
        if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force }
        "$label -> $dir $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & $exe stack $Archive -o $dir --group-filter StatueOfLibertyNebula_light_60s --group-temp-tolerance 2 --strategy InRamAllFrames `
            --no-plate-solve --output-format none --manifest $manifest --save-normalized *>> $log
        if ($LASTEXITCODE -ne 0) { throw "tianwen stack exited $LASTEXITCODE on $label" }
        "$label done $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
