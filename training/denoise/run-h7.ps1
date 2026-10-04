# H7 (docs/plans/denoiser-training.md, run log, "2026-10-05: H7, pre-registered"; #1260): a mono denoiser by E16b's arm
# recipe, held out by camera, against the shipped colour model given the mono plane as three equal channels. The plan's
# entry is the record; this header is its copy.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED 2026-10-05, before any H7 export, cache or model existed.
#
# POOL (2026-09-29-full, one-channel nights): the pinned test nights and the two ASI294MM Rosette plates the linearity
#   test refuses are out. E-cam: the QHY178M held out whole, Helix 2024-08-12 and Running Chicken 2023-03-17 primary,
#   Vela SNR 2022-12-03 read apart (its sky trains through the ASI294MM). E-night: the ASI1600MM luminance 2025-02-20
#   (camera and sky train through its Ha night). Trainer val: ASI294MM LMC 2021-12-29 and the QHY183M's 2024-03-02.
#   Train: arms\h7-train-12.txt, twelve nights, five cameras.
# ARM mono: degrade --mode noise --shape warped --cells 120 --draws 8 --seed 1 --noise-anchor master-calibration (the
#   mono shape at --warp-sigma-mono's default 0, #1243); bright cells by E16b's rule, up to 45 per training night, no
#   widening; prepare --channels 1, 45 per night, 120 per val night; E16b's training line; seeds 0..3. Training waits
#   for R2a's runner to exit (the owner, 2026-10-05).
# EVAL: one cache of the store's own tiles, --channels 1, 60 cells per eval night plus up to 20 bright off half B;
#   n2n_starsplit.py --per-session, orc (primary) and est. Models: mono_s0..3, and rgb = bb_e16barm_s2.pt (shipped)
#   given three equal channels (--replicate-mono).
# CHECKS (a failure stops the run): D1 all 14 export nights, failed 0, the cache one channel. D2 noise-check
#   --anchor master-calibration --bright-gate relative on the halved training nights (ASI294MM Cen A, M42, Lagoon;
#   ASI1600MM Ha). D3 each eval night solves and reads its sky level (< 0.15) in the 0-1 and 1-2 px bands.
# PREDICTIONS (orc, full strength, seed means, E-cam per field; bands 0-1 / 1-2 / 2-4 px):
#   (1) mono removes at least 15 percent on each E-cam field. Moderate.
#   (2) detail kept 1-2 px at every readable level at least 0.97. KILL below 0.92.
#   (3) mono's sky 0-1 px error left at least 0.05 below rgb's on both E-cam fields. Low.
#   (4) mono's sky 0-1 px error left on each E-cam field within 0.10 of its E-night value. Low.
#   (5) est against orc for mono: removal within 4 points over the four eval nights.
#   KILL for the arm: (2)'s kill, or (1) under 5 percent on both E-cam fields.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log.
#   $s = (Resolve-Path .\run-h7.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between stages or seeds, create C:\temp\e2\h7.stop; NEVER stop the process itself. Every stage is skipped
# when its output exists, so a re-launch resumes.
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-29-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    # On D:, since C: is shared with other sessions' scratch; prepare reads it once.
    [string]$Export = 'D:\tianwen-scratch\degraded\h7',
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe",
    [string]$Shipped = 'C:\temp\tianwen-scratch\n2n-e16b-arm\bb_e16barm_s2.pt',
    [int]$SeedCount = 4,
    [string]$Tag = 'h7',
    # R2a's runner: training starts only once it has exited (or its status no longer says running).
    [int]$WaitPid = 51532,
    [string]$WaitStatus = 'C:\temp\e2\r2.status'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir "$Tag.status"
$log = Join-Path $LogDir "$Tag.log"
$stopFile = Join-Path $LogDir "$Tag.stop"
$lists = Join-Path $LogDir "$Tag-lists"
$PID | Out-File (Join-Path $LogDir "$Tag.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
function Session-Args([string[]]$ids) { foreach ($id in $ids) { '--session'; $id } }
function Stop-Requested([string]$before) {
    if (Test-Path $stopFile) {
        "stop file present before $before; ending here" | Tee-Object -FilePath $log -Append
        "stopped $(Get-Date -Format o): before $before" | Out-File $status -Encoding utf8
        return $true
    }
    return $false
}
function Final([string]$dir, [string]$name) {
    $f = Join-Path $dir "${name}_final.pt"
    if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
}
function Invoke-Tianwen([string]$what, [string[]]$arguments, [string]$out) {
    & $Tianwen @arguments *>> $out
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE); see $out" }
}
Set-Status 'starting'

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    if (-not (Test-Path $Shipped)) { throw "the shipped checkpoint is not at $Shipped" }
    $shape = git -C $PSScriptRoot log -1 --format=%cI --grep "a mono master's injected noise takes a mono shape"
    if (-not $shape) { throw 'the mono noise shape (#1243) is not in this checkout' }
    $codeHead = git -C $PSScriptRoot log -1 --format=%cI -- ../../src
    $built = (Get-Item $Tianwen).LastWriteTime
    foreach ($t in @($shape, $codeHead)) {
        if ($built -lt [datetimeoffset]::Parse($t).LocalDateTime) { throw "the CLI was built $($built.ToString('o')), before $t; rebuild TianWen.Cli in Release" }
    }
    "H7 at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $($built.ToString('o'))" | Tee-Object -FilePath $log -Append
    New-Item -ItemType Directory -Force $lists | Out-Null
    $snap = Join-Path $LogDir "scripts-$Tag"
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-h7.ps1, arms\h7-*.txt $snap -Force

    $train = @(Read-List 'arms\h7-train-12.txt')
    $val = @(Read-List 'arms\h7-val-2.txt')
    $evalIds = @(Read-List 'arms\h7-eval-4.txt')
    if ($train.Count -ne 12 -or $val.Count -ne 2 -or $evalIds.Count -ne 4) { throw "the lists hold $($train.Count) / $($val.Count) / $($evalIds.Count), not 12 / 2 / 4" }

    # 1. The training nights' bright cells, their export sample left out.
    $brightList = Join-Path $lists 'bright-train.txt'
    if (-not (Test-Path $brightList)) {
        if (Stop-Requested 'bright-cell list') { return }
        Set-Status 'bright-cell list'
        Invoke-Tianwen 'bright-cells (train)' (@('dataset', 'bright-cells', '--bake', $Bake, '--per-session', '45', '--seed', '1',
            '--exclude-sample', '120', '--export-seed', '1', '--out', $brightList) + @(Session-Args $train)) $log
    }
    $bright = @(Get-Content $brightList | Where-Object { $_ -and -not $_.StartsWith('#') })
    $brightSessions = @($bright | ForEach-Object { ($_ -split "`t", 3)[2] } | Sort-Object -Unique)
    "bright cells: $($bright.Count) over $($brightSessions.Count) training nights" | Tee-Object -FilePath $log -Append

    # 2. One export: the training and val nights, their sample, and the training nights' bright cells.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -ne 'done') {
        if (Stop-Requested 'export') { return }
        Set-Status 'export'
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $exportArgs = @('dataset', 'degrade', '--bake', $Bake, '--out', $Export, '--mode', 'noise', '--shape', 'warped',
            '--draws', '8', '--seed', '1', '--noise-anchor', 'master-calibration', '--cells', '120', '--extra-cells', $brightList) +
            @(Session-Args ($train + $val))
        try { Invoke-Tianwen 'export' $exportArgs (Join-Path $Export 'degrade.log') }
        catch { "failed" | Out-File $exportStatus -Encoding utf8; throw }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }
    # D1, the export half: all fourteen nights, none failed.
    $summary = @(Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\] (\d+) sessions,.* failed (\d+)')
    if ($summary.Count -ne 1) { throw "D1: expected one export summary line, found $($summary.Count)" }
    $m = $summary[0].Matches[0]
    if ([int]$m.Groups[1].Value -ne 14 -or [int]$m.Groups[2].Value -ne 0) { throw "D1 failed: $($summary[0].Line)" }

    # 3. The training cache, mono.
    $cache = Join-Path $Scratch 'n2n-h7'
    if (-not (Test-Path (Join-Path $cache 'meta.json'))) {
        if (Stop-Requested 'prepare') { return }
        Set-Status 'prepare n2n-h7'
        & python n2n_smoke.py --prepare --channels 1 --root $Export --cache $cache --train-from-list arms\h7-train-12.txt `
            --val-from-list arms\h7-val-2.txt --cells-per-session 45 --val-cells-per-session 120 `
            --exclude-cells $brightList --extra-cells $brightList *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $cache failed (exit $LASTEXITCODE)" }
    }
    # D1, the cache half.
    $channels = (Get-Content (Join-Path $cache 'meta.json') -Raw | ConvertFrom-Json).channels
    "D1: export 14 nights, failed 0; the cache holds $channels channel(s)" | Tee-Object -FilePath $log -Append
    if ($channels -ne 1) { throw "D1 failed: the cache holds $channels channels" }

    # D2. The injected noise's level against the half pairs of the training nights that have them.
    $d2 = Join-Path $LogDir "$Tag-d2.txt"
    $halved = @($train | Where-Object { $_ -match 'Centaurus-A/2022-03-12|Great-Orion-Nebula/2021-11-28|Lagoon-and-Trifid/2022-06-28|ASI1600MM-Pro/Ha/' })
    if ($halved.Count -ne 4) { throw "D2: expected the four halved training nights, found $($halved.Count)" }
    if (-not (Test-Path $d2)) {
        if (Stop-Requested 'D2') { return }
        Set-Status 'D2 noise-check'
        & $Tianwen dataset noise-check --bake $Bake --cells 120 --seed 1 --extra-cells $brightList --anchor master-calibration --bright-gate relative @(Session-Args $halved) *> "$d2.partial"
        $d2Exit = $LASTEXITCODE
        Move-Item "$d2.partial" $d2 -Force
        Get-Content $d2 | Select-String '^\[noise-check\] (quiet|bright|PASS|FAIL)' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
        if ($d2Exit -ne 0) { throw "D2 failed (exit $d2Exit); see $d2" }
    }
    elseif (-not (Select-String -Path $d2 -Pattern '^\[noise-check\] PASS' -Quiet)) { throw "D2 failed earlier; see $d2" }

    # 4. The eval cache from the store's own tiles, with bright cells read off half B. A flip side is the same sky as
    #    its night and is not an eval night, so its cells (which the night's id matches as a substring) are dropped.
    $evalBright = Join-Path $lists 'bright-eval.txt'
    if (-not (Test-Path $evalBright)) {
        if (Stop-Requested 'eval bright cells') { return }
        Set-Status 'eval bright cells'
        $raw = "$evalBright.all"
        Invoke-Tianwen 'bright-cells (eval)' (@('dataset', 'bright-cells', '--bake', $Bake, '--per-session', '20', '--seed', '1',
            '--frame', 'halfmaster_b', '--out', $raw) + @(Session-Args $evalIds)) $log
        Get-Content $raw | Where-Object { $_ -notmatch '\|flip=' } | Set-Content $evalBright -Encoding utf8
    }
    $evalCache = Join-Path $Scratch 'n2n-h7-eval'
    if (-not (Test-Path (Join-Path $evalCache 'meta.json'))) {
        if (Stop-Requested 'prepare eval') { return }
        Set-Status 'prepare n2n-h7-eval'
        & python n2n_smoke.py --prepare --channels 1 --root $Bake --cache $evalCache --train-from-list arms\h7-eval-train-1.txt `
            --val-from-list arms\h7-eval-4.txt --cells-per-session 5 --val-cells-per-session 60 --extra-cells $evalBright *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $evalCache failed (exit $LASTEXITCODE)" }
    }

    # D3. Each eval night solves and reads its sky level in the 0-1 and 1-2 px bands (the reference line, no model run).
    $env:TIANWEN_CLI = (Resolve-Path $Tianwen).Path
    $env:TIANWEN_BAKES = $Bake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir "gaia\solved-$(Split-Path -Leaf $Bake)"
    $fields = @($evalIds | ForEach-Object { ($_ -split '\|', 2)[0] })
    $readable = @()
    foreach ($f in $fields) {
        $out = Join-Path $LogDir "$Tag-d3-$($f -replace '[/\\ ]', '_').txt"
        if (-not (Test-Path $out)) {
            if (Stop-Requested "D3 $f") { return }
            Set-Status "D3 $f"
            & python n2n_starsplit.py --cache $evalCache --anchor-only --only $f --per-session *> "$out.partial"
            if ($LASTEXITCODE -ne 0) { throw "D3 on $f failed (exit $LASTEXITCODE)" }
            Move-Item "$out.partial" $out -Force
        }
        $ref = (Select-String -Path $out -Pattern 'detail kept gauss1 ref:' | Select-Object -First 1).Line
        $sky = if ($ref) { (($ref -split 'ref:', 2)[1] -split '\|')[0] -split ',' } else { @('-', '-') }
        $ok = $sky[0].Trim() -ne '-' -and $sky[1].Trim() -ne '-'
        "D3 ${f}: sky level $(if ($ok) { 'readable' } else { 'UNREADABLE' })" | Tee-Object -FilePath $log -Append
        if ($ok) { $readable += $f }
    }
    $primary = @($fields | Where-Object { $_ -match 'Helix|Running-Chicken' })
    if (@($primary | Where-Object { $readable -notcontains $_ }).Count -gt 0) { throw "D3 failed: a primary E-cam field is unreadable" }

    # 5. Training waits for R2a (the owner, 2026-10-05).
    while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and ((Get-Content $WaitStatus -Raw) -match '^running')) {
        if (Stop-Requested 'training (waiting for R2a)') { return }
        Set-Status "checks passed; waiting for R2a's runner ($WaitPid) to exit before training"
        Start-Sleep -Seconds 300
    }
    "R2a's runner is done ($((Get-Content $WaitStatus -Raw).Trim())); training starts $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append

    foreach ($seed in 0..($SeedCount - 1)) {
        $name = "h7_mono_s$seed"
        if (Test-Path (Join-Path $cache "$name.pt")) { "train ${name}: present, skipped" | Tee-Object -FilePath $log -Append; continue }
        if (Stop-Requested "train $name") { return }
        Set-Status "train $name"
        "train $name $(Get-Date -Format o)" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond-map `
            --band-loss 3 --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 60000 `
            --val-every 500 --patience 4 --max-decays 4 --min-improve 0.001 --gate-every 500 `
            --seed $seed --out "$name.pt" *>> $log
        if ($LASTEXITCODE -ne 0) { throw "train $name failed" }
    }

    # 6. Score in both conditions, every eval night that read in D3.
    $models = @(foreach ($seed in 0..($SeedCount - 1)) { "mono_s$seed=$(Final $cache "h7_mono_s$seed")" }) + @("rgb=$Shipped")
    foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    "scoring with n2n_starsplit.py sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12))" | Tee-Object -FilePath $log -Append
    $failed = @()
    foreach ($cond in @('orc', 'est')) {
        $extra = @(if ($cond -eq 'orc') { '--plane-truth-anchor' })
        foreach ($f in $readable) {
            $out = Join-Path $LogDir "$Tag-score-$cond-$($f -replace '[/\\ ]', '_').txt"
            if (Test-Path $out) { continue }
            if (Stop-Requested "score $cond $f") { return }
            Set-Status "score $cond $f"
            & python n2n_starsplit.py --cache $evalCache --models @models --only $f --per-session --replicate-mono @extra *> "$out.partial"
            if ($LASTEXITCODE -ne 0) { $failed += "$cond $f (exit $LASTEXITCODE)" } else { Move-Item "$out.partial" $out -Force }
        }
    }
    $result = if ($failed) { "scoring failed: $($failed -join ', ')" } else { 'checked, trained and scored in both conditions' }
    "done $(Get-Date -Format o): $result" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
