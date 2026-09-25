# Probe BB, the per-field reading (run-bb-probe.ps1 is the probe; this reads its third rule properly).
#
# WHY. The probe's decision rule fired on ONE clause only: at matched removal the shipped wide_s2 spent
#   2.7 / 7.1 percent of extended-peak amplitude on the broadband fields against 0.4 / 1.0 on eval4b, while
#   stars and compact were no worse. Those are POOLED matched points, and arm X (2026-09-05) showed a pooled
#   matched point is a mixture of operating points: a model that leaves one field alone spends nothing on
#   its structure there. So the extended clause is read field by field (--only), on both caches, before it
#   is allowed to launch an arm. Nothing here is a new question; it is the probe's own rule applied at the
#   resolution the rule needs.
#
# WHAT WOULD CHANGE THE VERDICT. The extended gap is the model's (RUN stands) if wide_s2 spends clearly
#   more on extended at matched removal on the broadband fields field for field, including the one with real
#   nebulosity (Lagoon). It is the FIELD's (no arm; report it) if the excess sits only on the star-cloud
#   fields (both SMCs, the 24 mm Carina), where an "extended" peak is a blend of stars and costs what a star
#   costs, while Lagoon's extended reads like eval4b's.
#
# -Seeds (step 2's power, feedback "decompose the variance BEFORE training another arm"): the same eight
#   fields over every FINAL-weight seed on disk, wide s0 / s1 / s2_final and warped s0..s8, so the seed sd of
#   the per-field extended column sizes the arm instead of a default. Final weights only: s2 is the shipped
#   gate-selected checkpoint, and a chosen checkpoint is not comparable with unchosen ones.
#
# Run DETACHED, and read the status file, never the log:
#   $s = (Resolve-Path .\run-bb-perfield.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden     (add '-Seeds' for the spread)
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [switch]$Seeds
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$job = if ($Seeds) { 'bb-seeds' } else { 'bb-perfield' }
$status = Join-Path $LogDir "$job.status"
$log = Join-Path $LogDir "$job.log"
$PID | Out-File (Join-Path $LogDir "$job.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
Set-Status 'starting'

try {
    "probe bb $job at $(git -C $PSScriptRoot rev-parse --short HEAD)" | Tee-Object -FilePath $log -Append
    $models = if ($Seeds) {
        @("wide_s0=$Scratch\n2n-e2-wide\e2_wide_s0.pt",
          "wide_s1=$Scratch\n2n-e2-wide\e2_wide_s1.pt",
          "wide_s2f=$Scratch\n2n-e2-wide\e2_wide_s2_final.pt") +
        @(0..8 | ForEach-Object { "warped_s$_=$Scratch\n2n-e2-warped\e2_warped_s$_.pt" })
    }
    else {
        @("wide_s2=$Scratch\n2n-e2-wide\e2_wide_s2.pt",
          "warped_s2=$Scratch\n2n-e2-warped\e2_warped_s2.pt",
          "v19d_s2=$Scratch\n2n-d8\n2n_v19d_s2.pt")
    }
    foreach ($m in $models) { $p = $m.Split('=', 2)[1]; if (-not (Test-Path $p)) { throw "no checkpoint $p" } }
    # Each substring names exactly one scored session of its cache (the probe's tables list them).
    $fields = [ordered]@{
        'n2n-bb-eval4'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                            'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                            'eta-Car-Nebula/2026-02-20')
    }
    $n = 0
    foreach ($c in $fields.Keys) {
        if ($c -eq 'n2n-bb-eval4') {
            $env:TIANWEN_BAKES = $Bake
            $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
        }
        else {
            Remove-Item Env:TIANWEN_BAKES, Env:TIANWEN_SOLVED_MASTERS -ErrorAction SilentlyContinue
        }
        foreach ($f in $fields[$c]) {
            $n++
            $tag = ($f -replace '[/\\]', '_')
            Set-Status "$n/8 $c $f"
            "starsplit $c --only $f" | Tee-Object -FilePath $log -Append
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f `
                *> (Join-Path $LogDir "$job-$c-$tag.txt")
            if ($LASTEXITCODE -ne 0) { throw "starsplit failed on $c $f (exit $LASTEXITCODE)" }
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
