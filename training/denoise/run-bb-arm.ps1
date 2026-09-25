# Probe BB step 2, the broadband arm (docs/plans/denoiser-training.md, "Probe BB"). run-bb-probe.ps1 and
# run-bb-perfield.ps1 are step 1 and decided that this runs.
#
# ---------------------------------------------------------------------------------------------------
# PRE-REGISTERED, before the export. Written 2026-09-25.
#
# THE FINDING THIS TESTS. At matched noise removal the shipped e2_wide_s2 spends 1.5 to 3.9 percent of
#   extended-peak amplitude at 4 percent removed (4.1 to 9.9 at 10) on the four broadband fields of
#   arms/bb-eval-4.txt, against 0.1 to 1.0 (0.4 to 2.1) on the four narrowband fields of eval4b: no
#   overlap, field for field, Lagoon's real nebulosity included. Stars and compact detail cost the same
#   on both, full-strength removal is 18.5 to 34.2 percent on the broadband fields (none adds noise), and
#   v19d, the pre-E2 model, spends 3 to 4 at 4 percent everywhere. So the E2 recipe's structure economy
#   holds on narrowband and does not carry to broadband.
#   Not "the pool never saw broadband": WIDE's 17 sessions include eight panels of the 2024 Vela SNR
#   mosaic, IDAS LPS-D3 on the ASI533MC. It saw ONE broadband mosaic, one rig, one night's settings.
#
# ARMS, from ONE export of 2026-09-25-full (so a difference is the train list and nothing else):
#   control  arms/bb-ctl-14.txt: WIDE re-read on this bake's ids, minus the three sessions it holds out as
#            TEST. Not the shipped wide seeds: they differ from any new arm in bake, in those three
#            sessions and in the arm's additions at once.
#   arm      bb-ctl-14 plus arms/bb-add-10.txt: ten ASI533MC broadband sessions (UV/IR cut and IDAS
#            LPS-D3, 35 to 135 mm, star cloud, globular, emission and dark nebulae), every one inside
#            WIDE's plane range, so the plane coverage E9 moved is held fixed.
#   Both: the E2 / E9 recipe byte for byte (--synthetic, l2, upsample, cond, band loss 3 at "2,4 4,8",
#   base 32, 4000 steps, gate every 100), 45 cells per train session, val arms/bb-val-2.txt at 120
#   (E2's two gate sessions on this bake's ids), export seed 1, --warp-sigma 0.5, 8 draws, 120 cells.
#   Cells are seeded per session NAME, so the fourteen shared sessions carry the same cells and drawn
#   depths in both caches. The arm has 1080 train cells to the control's 630: a second difference,
#   stated as E9 stated its own.
#   Seeds interleaved (control s0, arm s0, control s1, ...), so an early stop leaves the arms balanced.
#   Compared at FINAL weights throughout (<name>_final.pt where the gate passed, else the output, which is
#   then final): a chosen checkpoint is not comparable with an unchosen one.
#
# PRIMARY. M = the mean over the four bb-eval-4 fields of the per-field (--only) extended amplitude
#   spent at 10 percent removed (4 percent where a seed never reaches 10 on a field, for both arms on that
#   field). Effect = arm minus control, per-arm means over seeds.
#   PREDICTION: the arm lowers M by at least d = 2.4 points, half the shipped model's broadband-over-
#   narrowband gap (6.0 against 1.15), while (a) stars and compact at matched removal on bb-eval-4,
#   (b) full-strength removal on each broadband field and (c) eval4b's per-field extended column all stay
#   within the control's seed spread. Confidence: low. WIDE already held eight broadband panels and still
#   spends on broadband extended peaks like on stars, which points at the field's CONTENT at least as
#   much as at the filter.
#   KILL: the 95 percent interval of the effect excludes -d (broadband diversity on a known sensor does
#   not buy half the gap). Then the next suspect is what the extended column IS on a broadband field: on
#   the 24 mm Carina it costs exactly what a star costs (9.9 against 9.8 at 10 percent), the signature of
#   blended stars rather than nebulosity, and that is a metric question before it is a training one.
#
# SEEDS. N = ceil(2 (1.96 + 0.84)^2 sd^2 / d^2), at least 6 and at most 12 per arm, where sd is the seed
#   sd of M over the final-weight seeds already on disk (run-bb-perfield.ps1 -Seeds: wide s0 / s1 / s2
#   final and warped s0..s8), measured before any arm checkpoint exists. The launch line records N.
#
# WHAT IT CANNOT SETTLE. The rig axis: both arms' broadband sessions are all ASI533MC, so a null here
#   leaves "a broadband arm on the eval sensors' own rigs" open, and a win cannot say whether ten sessions
#   of another sensor would have done it. Four eval fields on three rigs, as step 1 said.
# ---------------------------------------------------------------------------------------------------
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\bb-arm.pid.
#   $s = (Resolve-Path .\run-bb-arm.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s,'-SeedCount','8' -WindowStyle Hidden
#   (-ExportOnly stops after the export and both prepares.)
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Export = 'D:\Astro-Dataset\degraded\bb-arm',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    [int]$SeedCount = 0,
    [int]$FirstSeed = 0,
    [switch]$ExportOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $ExportOnly -and ($SeedCount -lt 1 -or $SeedCount -gt 12)) {
    throw "-SeedCount must be 1 to 12 (the pre-registration sizes it), or pass -ExportOnly"
}
if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$status = Join-Path $LogDir 'bb-arm.status'
$log = Join-Path $LogDir 'bb-arm.log'
$PID | Out-File (Join-Path $LogDir 'bb-arm.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
Set-Status 'starting'

function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}

try {
    "bb-arm at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $((Get-Item $Tianwen).LastWriteTime.ToString('o')), seeds $FirstSeed..$($FirstSeed + $SeedCount - 1)" |
        Tee-Object -FilePath $log -Append
    $snap = Join-Path $LogDir 'scripts-bb-arm'
    New-Item -ItemType Directory -Force $snap | Out-Null
    Copy-Item *.py, run-bb-arm.ps1 $snap -Force
    Copy-Item arms\bb-ctl-14.txt, arms\bb-add-10.txt, arms\bb-val-2.txt, arms\bb-eval-4.txt $snap -Force
    # The arm's train list is the two files together; n2n_smoke takes one, so it is written here, once.
    $armList = Join-Path $snap 'bb-arm-24.txt'
    @(Read-List 'arms\bb-ctl-14.txt') + @(Read-List 'arms\bb-add-10.txt') | Out-File $armList -Encoding utf8

    # 1. Export: the 26 sessions, E9's flags. A finished export is skipped on re-run.
    $exportStatus = Join-Path $Export 'degrade.status'
    $state = if (Test-Path $exportStatus) { (Get-Content $exportStatus -Raw).Trim() } else { 'missing' }
    if ($state -eq 'done') {
        "export : $Export already done, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        Set-Status 'export'
        New-Item -ItemType Directory -Force $Export | Out-Null
        "running $(Get-Date -Format o)" | Out-File $exportStatus -Encoding utf8
        $sessions = @(Read-List $armList) + @(Read-List 'arms\bb-val-2.txt')
        $sessionArgs = @()
        foreach ($sid in $sessions) { $sessionArgs += '--session'; $sessionArgs += $sid }
        "export -> $Export ($($sessions.Count) sessions, warped, --warp-sigma 0.5, seed 1)" | Tee-Object -FilePath $log -Append
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export --mode noise --shape warped `
            --warp-sigma 0.5 --draws 8 --cells 120 --seed 1 --measure-shape @sessionArgs *>> (Join-Path $Export 'degrade.log')
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $exportStatus -Encoding utf8; throw "export failed (exit $LASTEXITCODE)" }
        "done" | Out-File $exportStatus -Encoding utf8
        Get-Content (Join-Path $Export 'degrade.log') | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } | Tee-Object -FilePath $log -Append
    }

    # 2. Prepare both caches from the one export, with run-wide.ps1's staleness gate.
    $arms = [ordered]@{ ctl = 'arms\bb-ctl-14.txt'; arm = $armList }
    foreach ($a in $arms.Keys) {
        $cache = Join-Path $Scratch "n2n-bb-$a"
        $meta = Join-Path $cache 'meta.json'
        if (Test-Path $meta) {
            $rows = Join-Path $Export 'degradations.jsonl'
            if ((Get-Item $rows).LastWriteTimeUtc -gt (Get-Item $meta).LastWriteTimeUtc) {
                throw "cache $cache is older than the export it came from ($rows). Delete the cache and re-run; a prepared cache is never edited in place."
            }
            "prepare $a : cache present and newer than the export, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        Set-Status "prepare $a"
        "prepare $a -> $cache" | Tee-Object -FilePath $log -Append
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list $arms[$a] --val-from-list arms\bb-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $a failed (exit $LASTEXITCODE)" }
    }
    if ($ExportOnly) { "done (export only) $(Get-Date -Format o)" | Out-File $status -Encoding utf8; return }

    # 3. Train, interleaved so the arms stay balanced however far it gets.
    foreach ($seed in $FirstSeed..($FirstSeed + $SeedCount - 1)) {
        foreach ($a in $arms.Keys) {
            $cache = Join-Path $Scratch "n2n-bb-$a"
            $out = "bb_${a}_s$seed.pt"
            if (Test-Path (Join-Path $cache $out)) {
                "train $a seed $seed : checkpoint present, skipped" | Tee-Object -FilePath $log -Append
                continue
            }
            Set-Status "train $a seed $seed"
            "train $a seed $seed -> $out (--synthetic)" | Tee-Object -FilePath $log -Append
            & python n2n_smoke.py --train --cache $cache --synthetic --loss l2 --upsample --cond `
                --band-loss 3 --band-scales "2,4 4,8" --base 32 --steps 4000 --gate-every 100 `
                --seed $seed --out $out *>> $log
            if ($LASTEXITCODE -ne 0) { throw "train $a failed for seed $seed" }
        }
    }

    # 4. Score every FINAL-weight checkpoint of both arms, per field, on the two eval caches.
    $models = @()
    foreach ($seed in $FirstSeed..($FirstSeed + $SeedCount - 1)) {
        foreach ($a in $arms.Keys) {
            $cache = Join-Path $Scratch "n2n-bb-$a"
            $final = Join-Path $cache "bb_${a}_s${seed}_final.pt"
            $p = if (Test-Path $final) { $final } else { Join-Path $cache "bb_${a}_s$seed.pt" }
            $models += "${a}_s$seed=$p"
        }
    }
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
            Set-Status "score $n/8 $c $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f `
                *> (Join-Path $LogDir "bb-arm-score-$c-$($f -replace '[/\\]', '_').txt")
            if ($LASTEXITCODE -ne 0) { throw "score failed on $c $f (exit $LASTEXITCODE)" }
        }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
