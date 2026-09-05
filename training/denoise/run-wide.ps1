# Arm WIDE (docs/plans/denoiser-training.md, run log 2026-09-05 "arm X" and section 8): does covering
# the CONDITIONING plane's deployed range make the supervised warped arm act on the fields it currently
# leaves alone?
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the export. Written 2026-09-05.
#
# THE FINDING THIS TESTS. The conditioning plane is the input's background sigma in the stretched
#   domain (median minus the 25th percentile of the luminance, x100). The E2 arms' injected inputs
#   never go below 0.23 on it, because the eight training masters sit at 0.19 to 0.49 and the injected
#   floor is the master's own plane; 15 of the 76 colour sessions in 2026-09-full deploy under 0.23
#   (down to 0.07). On the two eval4 fields under it, warped_s2 removes 2.8 percent of the noise at full
#   strength against 20 percent pooled on eval4b (condpool.py, condprobe.py).
#
# ARM. S-warped, the E2 recipe byte for byte (--synthetic, l2, upsample, cond, band loss 3 at
#   "2,4 4,8", base 32, 4000 steps, gate every 100), on E2's eight sessions PLUS the nine low-plane
#   sessions of arms/wide-low-9.txt (Pleiades 0.07 and eight 2024 Vela SNR panels at 0.08 to 0.15),
#   45 cells each; val unchanged (arms/e2-val-2.txt, Triangulum at 0.10 is the gate). Export seed 1,
#   --warp-sigma 0.5, 120 cells and 8 draws per session as E2, so the eight shared sessions carry the
#   SAME cells and drawn depths as warped and the arm differs from it in the nine sessions only.
#
# PREDICTION: at full strength on eval4's Horsehead (plane 0.09) and Skull-and-Crossbones (0.17) cells,
#   wide removes at least 10 percent of the noise where warped_s2 removes 2.8, and at matched removal on
#   eval4b (fields inside both ranges) its stars / compact / extended columns sit within warped's seed
#   spread. Confidence: moderate for the first half (a net that has seen the plane's value acts on it),
#   low for the second (765 cells against 360 is a second change, and eight of the nine new sessions
#   are one 2024 mosaic).
#   KILL: wide removes under 5 percent on Horsehead and Skull at full strength. Then the range is not
#   why the supervised arms sit still there, and the next suspect is the field itself (a dense or very
#   smooth scene the darkest-half estimator reads differently), not the training distribution.
#
# WHAT IT CANNOT SETTLE: a wide that is ALSO better than warped on eval4b at matched removal could be
#   the pool (more cells, a broadband-looking 2024 rig) rather than the range; v24 measured the pool draw
#   carrying more variance than most effects chased here. That is why the second half of the prediction
#   is "within seed spread" and not "better", and why a win there is reported, not claimed.
#
# NOT predicted, watched: the shape calibration. --warp-sigma 0.5 was measured on 2025-2026-organized;
#   --measure-shape re-measures band1/band0 on THIS bake's own real pairs, and if the injected 0.46 no
#   longer sits on the real half-master's figure that is a per-bake fact to record, not a reason to
#   re-tune mid-arm.
# ---------------------------------------------------------------------------------------------------
#
# Run detached; read the status file, never the log:
#   $s = "$PWD\run-wide.ps1"
#   Start-Process pwsh -ArgumentList '-NoProfile','-Command',"& '$s'" -WindowStyle Hidden
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-full',
    [string]$Export = 'D:\Astro-Dataset\degraded\wide',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int[]]$Seeds = @(0, 1, 2),
    [switch]$ExportOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($s in $Seeds) {
    if ($s -lt 0 -or $s -gt 999) {
        throw "Seed '$s' is out of range. Under pwsh -File, '-Seeds 3,4,5' arrives as the single string '3,4,5' and coerces to 345; use -Command instead."
    }
}
if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'wide.status'
$log = Join-Path $LogDir 'wide.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8

$snap = Join-Path $LogDir 'scripts-wide'
New-Item -ItemType Directory -Force $snap | Out-Null
Copy-Item *.py, run-wide.ps1 $snap -Force
Copy-Item arms\e2-train-8.txt, arms\e2-val-2.txt, arms\wide-*.txt $snap -Force

function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}

try {
    # 1. Export: the nineteen sessions the arm needs and no others, by exact session id through the
    #    degrade command's --session filter (added for this arm; the E2 export of the whole organized
    #    bake ran two and a half hours). A finished export is skipped on re-run.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -eq 'done') {
        "export : $Export already done, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $sessions = @(Read-List 'arms\wide-train-17.txt') + @(Read-List 'arms\e2-val-2.txt')
        $sessionArgs = @()
        foreach ($sid in $sessions) { $sessionArgs += '--session'; $sessionArgs += $sid }
        "export -> $Export ($($sessions.Count) sessions, warped, --warp-sigma 0.5, seed 1)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export --mode noise --shape warped `
            --warp-sigma 0.5 --draws 8 --cells 120 --seed 1 --measure-shape @sessionArgs *>> (Join-Path $Export 'degrade.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }
    if ($ExportOnly) { "done (export only) $(Get-Date -Format o)" | Out-File $status -Encoding utf8; return }

    # 2. Prepare, with run-e2.ps1's staleness gate: a cache older than its export is refused, not reused.
    $cache = Join-Path $Scratch 'n2n-e2-wide'
    $meta = Join-Path $cache 'meta.json'
    if (Test-Path $meta) {
        $rows = Join-Path $Export 'degradations.jsonl'
        if ((Get-Item $rows).LastWriteTimeUtc -gt (Get-Item $meta).LastWriteTimeUtc) {
            throw "cache $cache is older than the export it came from ($rows). Delete the cache and re-run; a prepared cache is never edited in place."
        }
        "prepare : cache already present and newer than the export, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        "prepare -> $cache" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\wide-train-17.txt --val-from-list arms\e2-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed" }
    }

    # 3. Train: the E2 recipe, --synthetic, three seeds.
    foreach ($seed in $Seeds) {
        $out = "e2_wide_s$seed.pt"
        if (Test-Path (Join-Path $cache $out)) {
            "train wide seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        "train wide seed $seed -> $out (--synthetic)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond `
            --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 `
            --seed $seed --out $out *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train failed for seed $seed" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
