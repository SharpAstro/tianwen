# R2a (docs/plans/star-remover-training.md, section 6, R2): the star remover's first training, on the R1 training
# export, the shipped denoiser's recipe (convmapb_s2's training line: L2 on the rim-masked tile, the 2-4 and 4-8 px
# DoG bands at 3, the stored per-pixel noise plane as the conditioning input, plateau schedule) regressing an injected
# draw onto its starless plate.
#
# WHAT THIS IS, AND IS NOT. Three arms from one export binary: random (the field profile, the training arm), gaussian
# (H2: the profile's shape) and at-site (H3's control: the stars put back where R0 took them out), three seeds each,
# interleaved so a stop leaves matched sets. It is NOT yet the plan's full recipe: the speckle teacher (a port of
# StarlessSpeckles with its parity fixture) and the stars plate's flux penalty (H5) are not written, so these models are
# taught L2 and bands alone and SCORED for speckles. Nothing here is read until R2's eval exists (completeness by flux bin,
# background under the injected footprints, speckles per band against their null, 1:1 spot checks); its predictions are
# registered in the plan with the eval, before any of these models is scored.
#
# DATA. C:/temp/tianwen-scratch/r1-train/<arm> (run-r1-train.ps1 in C:/temp/e2: 138 sessions asked, 136 written, 40 cells
# and 4 draws each). The trainer is 3-channel, so the 15 mono sessions drop out (H7). Split by NIGHT: arms\r2-train.txt
# (113) and arms\r2-val.txt (8 ids, 4 nights), the same names for every arm. --gate-every 0: the denoiser's selection gate
# keeps only a checkpoint that cuts the noise, which a remover must not do.
#
# Run DETACHED; read the status file, never the log. To stop it between stages or runs, create C:\temp\e2\r2.stop; NEVER
# stop the process itself. Every stage is skipped when its output exists, so a re-launch resumes. An arm waits until its
# export's last chunk has exited 0 (C:/temp/e2/r1-train.log), so this may start while the export still runs.
#   $s = (Resolve-Path .\run-r2.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
param(
    [string]$Export = 'C:/temp/tianwen-scratch/r1-train',
    [string]$ExportLog = 'C:/temp/e2/r1-train.log',
    [string]$LastChunk = '130',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string[]]$Arms = @('random', 'gaussian', 'at-site'),
    [int]$SeedCount = 3,
    [string]$Tag = 'r2',
    # Free space kept on the cache's drive after a cache is written (one arm's cache is about 27 GiB).
    [int]$ReserveGb = 15
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir "$Tag.status"
$log = Join-Path $LogDir "$Tag.log"
$stopFile = Join-Path $LogDir "$Tag.stop"
$PID | Out-File (Join-Path $LogDir "$Tag.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Stop-Requested([string]$before) {
    if (Test-Path $stopFile) {
        "stop file present before $before; ending here" | Tee-Object -FilePath $log -Append
        "stopped $(Get-Date -Format o): before $before" | Out-File $status -Encoding utf8
        return $true
    }
    return $false
}
# An arm's export is complete once its last chunk exited 0; a chunk that exited otherwise stops the run.
function Export-Ready([string]$arm) {
    $lines = @(Get-Content $ExportLog | Select-String -SimpleMatch " $arm-")
    $bad = @($lines | Where-Object { $_.Line -match "$([regex]::Escape($arm))-\d+ exit (\d+)$" -and $Matches[1] -ne '0' })
    if ($bad) { throw "the $arm export has a failed chunk: $($bad[0].Line)" }
    return [bool]($lines | Where-Object { $_.Line.EndsWith("$arm-$LastChunk exit 0") })
}
Set-Status 'starting'

try {
    "R2a at $(git -C $PSScriptRoot rev-parse --short HEAD), $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
    $draws = Select-String -Path n2n_smoke.py -SimpleMatch 'draws = int(meta.get("draws", SUBS_PER_CELL))' -Quiet
    if (-not $draws) { throw 'this n2n_smoke.py samples all eight sub slots; the 4-draw export needs the draws fix' }
    $snap = Join-Path $LogDir "scripts-$Tag"
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-r2.ps1, arms\r2-train.txt, arms\r2-val.txt $snap -Force

    :seeds foreach ($seed in 0..($SeedCount - 1)) {
        foreach ($arm in $Arms) {
            $cache = Join-Path $Scratch "r2-$arm"
            $name = "r2_$($arm -replace '-', '')_s$seed"
            if (Test-Path (Join-Path $cache "$name.pt")) { "train ${name}: present, skipped" | Tee-Object -FilePath $log -Append; continue }

            # The arm's export, waited for.
            while (-not (Export-Ready $arm)) {
                if (Stop-Requested "waiting for the $arm export") { return }
                Set-Status "waiting for the $arm export"
                Start-Sleep -Seconds 300
            }

            # The arm's cache, once.
            if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
                if (Stop-Requested "prepare $arm") { return }
                $free = (Get-PSDrive ((Resolve-Path $Scratch).Drive.Name)).Free / 1GB
                if ($free -lt 27 + $ReserveGb) { throw "prepare ${arm}: $([int]$free) GB free on the cache's drive, under the 27 GB a cache takes plus the $ReserveGb kept" }
                Set-Status "prepare $arm"
                & python n2n_smoke.py --prepare --root (Join-Path $Export $arm) --cache $cache --train-from-list arms\r2-train.txt `
                    --val-from-list arms\r2-val.txt --cells-per-session 40 --val-cells-per-session 40 *>> $log
                if ($LASTEXITCODE -ne 0) { throw "prepare $arm failed (exit $LASTEXITCODE)" }
                $meta = Get-Content (Join-Path $cache 'meta.json') -Raw | ConvertFrom-Json
                if ($meta.draws -ne 4 -or -not $meta.injected) { throw "the $arm cache holds $($meta.draws) draws, injected=$($meta.injected); expected 4 injected" }
            }

            if (Test-Path $stopFile) { "stop file present before $name" | Tee-Object -FilePath $log -Append; break seeds }
            Set-Status "train $name"
            "train $name $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond-map `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
                --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 0 `
                --seed $seed --out "$name.pt" *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train $name failed (exit $LASTEXITCODE)" }
            "train $name done $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    "FAILED: $_" | Tee-Object -FilePath $log -Append
    throw
}
