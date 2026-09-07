# P2 E2.8b: can the star term be held to the truth's EMPTY sky?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the run. Written 2026-09-07 from the plan's E2.8 results section.
#
# WHY. E2.8's star term passed its gate on every seed (out/truth 1.10 to 1.17 holding 0.84 to 0.95 of
#   the truth's stars against a control that never selected) and FABRICATED on the observer session:
#   34 to 45 times the truth's star count, widths under 1.0. The observer's truth is three times
#   quieter than the gate session's and its INPUT already reads 6.9x the truth's detections at the
#   truth-anchored threshold; the control denoised that to 0.9, the star arm took it to 47 by step 100.
#   The term rewards a matched peak, flux and concentration at detected stars and says nothing about
#   the rest of the frame, so sharpening EVERY peak is the cheapest way to satisfy it.
#
# WHAT IT IS. Two arms, each E2.8's recipe plus one thing, five seeds each, the same cache; E2.8's five
#   runs (p2star_s*.pt, p2-star.log) are the paired control.
#     N  --star-loss-empty       the counterpart: the same three ratios over as many EMPTY windows a
#                                tile as it has stars (empty.npy from --prepare-stars: no low-bar
#                                detection within the 7x7 plus one pixel, window max under six MAD),
#                                each weighted as a star, so a raised peak over empty target costs what
#                                a lowered star peak costs.
#     W  --star-loss-refix 400   E2.8's term, the weight re-matched to the pixel term ONCE at step 400
#                                and fixed again; the first batch's pixel term is the injected noise,
#                                not the task, and matching there left the term tens of times the pixel
#                                term at plateau.
#
# PREDICTION. Arm N holds the selecting session's result (selected out/truth <= 1.20, stars at the
#   minimum >= 0.80) and brings the observer's star count under 2x its input null of 6.9 (so under 14)
#   with the observer width back over 1.0. Arm W narrows less on the gate session (selected 1.20 to
#   1.30) and improves the observer only partly (10 to 25x). Confidence MODERATE on N, LOW on W.
# PRIMARY READOUT: the observer columns (n2n_gatelog.py: obs0 out/truth and stars at the selected step).
# KILL LINE: neither arm brings the observer under 2x its input null while keeping the gate session's
#   selection under 1.30. Then a per-star term cannot be made honest by construction and the fork
#   reopens on the OBJECTIVE, not the capacity.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-p2-starloss-b.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [int[]]$Seeds = @(0, 1, 2, 3, 4)
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 0,1,2' arrives as the single string '0,1,2' and coerces to 12; use -Command instead."
    }
}

New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'p2-star-b.status'
$log = Join-Path $LogDir 'p2-star-b.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$snap = Join-Path $LogDir 'scripts-p2-star-b'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-p2-starloss-b.ps1 $snap -Force

$cache = Join-Path $Scratch 'n2n-p2-blur'
if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
    throw "no cache at $cache. E2.6 built it; this arm deliberately does not rebuild it."
}

$arms = @(
    @{ Name = 'N'; Extra = @('--star-loss-empty');        Note = 'no-star counterpart over empty target windows' }
    @{ Name = 'W'; Extra = @('--star-loss-refix', '400'); Note = 'weight re-fixed to the pixel term at step 400' }
)

try {
    if (-not (Test-Path (Join-Path $cache 'empty.npy'))) {
        "prepare-stars on $cache (writes stars.npy and empty.npy)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare-stars --cache $cache *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare-stars failed" }
    }

    foreach ($arm in $arms) {
        "=== arm $($arm.Name): E2.8 + $($arm.Extra -join ' ')  ($($arm.Note))" | Tee-Object -FilePath $log -Append
        foreach ($seed in $Seeds) {
            $out = "p2star$($arm.Name)_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train arm $($arm.Name) seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            "train arm $($arm.Name) seed $seed -> $out" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --cond-psf01 --loss l2 --upsample `
                --band-loss 3 --band-scales "1,2" --base 32 --steps 4000 --gate-every 100 `
                --star-loss auto @($arm.Extra) --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train failed for arm $($arm.Name) seed $seed" }
        }
    }

    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
