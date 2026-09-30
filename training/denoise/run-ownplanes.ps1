# The eval fields on their OWN planes: the three eval caches of E14/E16a prepared again, from D:/Astro-Dataset/
# 2026-09-28-evalplanes, the eval sessions re-baked by a build whose tile export records each frame's own stretch and
# writes each tile's plane from that frame's own noise estimate ("every tile carries its own frame's stretch and noise
# plane"). Until then a half's plane borrowed the master's stretch and an assumed depth of sqrt 2.
#
# Steps, each skipped when its output exists:
#   1. prepare n2n-{bb-eval4,e2-eval4b,eval4}-rfp, the lists and cell counts of run-evalrf-planes.ps1, planes from the
#      bake itself;
#   2. say whether each holds the -rf cache's cells and half-A tiles (the same cells if the stacking reproduced);
#   3. the estimate against the truth per field (n2n_starsplit.py --anchor-only), beside E16a's factors;
#   4. score convmap_s0..3 as the product would condition it (no anchor) and under the oracle anchor, with convrf_s0..3
#      and the shipped model, into ownplanes-score-{est,orc}-*.txt.
#
# Run DETACHED; read the status file, never the log. To stop between steps, create C:\temp\e2\<Tag>.stop.
#   $s = (Resolve-Path .\run-ownplanes.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# The per-channel re-bake ("the noise estimator anchors each channel on its own noise") is the same run on another
# bake, into other caches and files:
#   ... -ArgumentList '-NoProfile','-File',$s,'-Bake','D:/Astro-Dataset/2026-09-28-evalplanes-pc','-CacheSuffix','-rfpc','-Tag','perch'
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-28-evalplanes',
    [string]$CacheSuffix = '-rfp',
    [string]$Tag = 'ownplanes',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe"
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
Set-Status 'starting'

try {
    if (-not (Test-Path (Join-Path $Bake 'tiles-manifest.jsonl'))) { throw "no bake at $Bake" }
    "$Tag at $(git -C $PSScriptRoot rev-parse --short HEAD) on $Bake" | Tee-Object -FilePath $log -Append
    $caches = @(
        @{ Name = 'n2n-bb-eval4'; Train = 'arms\bb-eval-train-2.txt'; Val = 'arms\bb-eval-4.txt'; Cells = 60
           Fields = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03', 'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19') },
        @{ Name = 'n2n-e2-eval4b'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4b-25full.txt'; Cells = 60
           Fields = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18', 'eta-Car-Nebula/2026-02-20') },
        @{ Name = 'n2n-eval4'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4-25full.txt'; Cells = 48
           Fields = @('Rim-Nebula/2025-05-02', 'Horsehead-Nebula/2025-10-28', 'Skull-and-Crossbones-Nebula/2026-02-14') })

    # 1 and 2. Prepare, then compare with the -rf cache every earlier model was scored on.
    foreach ($c in $caches) {
        $cache = Join-Path $Scratch "$($c.Name)$CacheSuffix"
        if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
            if (Stop-Requested "prepare $($c.Name)$CacheSuffix") { return }
            Set-Status "prepare $($c.Name)$CacheSuffix"
            & python n2n_smoke.py --prepare --root $Bake --cache $cache --train-from-list $c.Train `
                --val-from-list $c.Val --cells-per-session 5 --val-cells-per-session $c.Cells *>> $log
            if ($LASTEXITCODE -ne 0) { throw "prepare $($c.Name)$CacheSuffix failed (exit $LASTEXITCODE)" }
        }
        $same = & python -c "import sys, numpy as np, n2n_smoke as S; a, ma = S.open_cache(sys.argv[1]); b, mb = S.open_cache(sys.argv[2]); k = ma['keys'] == mb['keys']; print(k, k and bool(np.array_equal(a[:, S.SLOT_HALF_A], b[:, S.SLOT_HALF_A])), mb.get('sigma_planes', 0))" `
            (Join-Path $Scratch "$($c.Name)-rf") $cache
        "$($c.Name)$CacheSuffix against -rf: same cell keys, same half-A tiles, planes: $same" | Tee-Object -FilePath $log -Append
    }

    # 3. The estimate against the truth, per field.
    $env:TIANWEN_CLI = (Resolve-Path $Tianwen).Path
    $env:TIANWEN_BAKES = $Bake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir "gaia\solved-$(Split-Path -Leaf $Bake)"
    foreach ($c in $caches) {
        $out = Join-Path $LogDir "$Tag-anchor-$($c.Name).txt"
        if (Test-Path $out) { continue }
        if (Stop-Requested "anchor $($c.Name)") { return }
        Set-Status "anchor $($c.Name)"
        & python n2n_starsplit.py --cache (Join-Path $Scratch "$($c.Name)$CacheSuffix") --anchor-only *> $out
        if ($LASTEXITCODE -ne 0) { throw "anchor $($c.Name) failed (exit $LASTEXITCODE)" }
        Get-Content $out | Select-String 'plane truth anchor' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }

    # 4. Score, as the product would condition convmap and under the oracle anchor.
    $mapped = foreach ($s in 0..3) { "convmap_s$s=$(Join-Path $Scratch "n2n-bb-ctl-rfm\bb_ctlmap_s$s.pt")" }
    $others = @(foreach ($s in 0..3) { "convrf_s$s=$(Join-Path $Scratch "n2n-bb-ctl-rf\bb_ctlrf_s${s}_final.pt")" }) +
              @("shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')")
    foreach ($m in @($mapped) + $others) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    "scoring with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" | Tee-Object -FilePath $log -Append
    $failed = @()
    foreach ($cond in @('est', 'orc')) {
        $models = if ($cond -eq 'est') { @($mapped) + $others } else { @($mapped) }
        $extra = @(if ($cond -eq 'orc') { '--plane-truth-anchor' })
        foreach ($c in $caches) {
            foreach ($f in $c.Fields) {
                $out = Join-Path $LogDir "$Tag-score-$cond-$($c.Name)-$($f -replace '[/\\ ]', '_').txt"
                if (Test-Path $out) { continue }
                if (Stop-Requested "score $cond $f") { return }
                Set-Status "score $cond $($c.Name) $f"
                & python n2n_starsplit.py --cache (Join-Path $Scratch "$($c.Name)$CacheSuffix") --models @models --only $f --per-session @extra *> $out
                if ($LASTEXITCODE -ne 0) { $failed += "$cond $f (exit $LASTEXITCODE)" }
            }
        }
    }
    $summary = if ($failed) { "scoring failed: $($failed -join ', ')" } else { 'prepared, anchored and scored' }
    "done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
