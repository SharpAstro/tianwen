# E14's ring-fixed eval (run-poolrf.ps1, deviation 2026-09-27 17:15), one chain:
#   1. wait for the old-domain scoring (it plate-solves through the Release CLI this chain rebuilds);
#   2. rebuild the Release CLI at HEAD, so the bake launcher's staleness check passes;
#   3. copy the six affected masters aside, then restack those six sessions in 2026-09-25-full
#      (--resume --rebuild-session), whose tile export now carries the ring fix;
#   4. compare each rebuilt master with its old copy: the fix is in the tile export, so they should be equal,
#      and a master that is NOT equal has its cached plate solve removed so the star mask solves it afresh;
#   5. prepare n2n-bb-eval4-rf, n2n-e2-eval4b-rf and n2n-eval4-rf from the bake;
#   6. run the rf-domain scoring (run-poolrf-score.ps1 -Domain rf).
#
# Run DETACHED; read the status file, never the log. Job files: C:\temp\e2\e14evalrf.{pid,status,log}.
#   $s = (Resolve-Path .\run-poolrf-evalrf.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between steps, create C:\temp\e2\e14evalrf.stop; NEVER stop the process itself.
param(
    [string]$Bake = 'D:\Astro-Dataset\2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$status = Join-Path $LogDir 'e14evalrf.status'
$log = Join-Path $LogDir 'e14evalrf.log'
$stopFile = Join-Path $LogDir 'e14evalrf.stop'
$PID | Out-File (Join-Path $LogDir 'e14evalrf.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Say([string]$what) { "$(Get-Date -Format o) $what" | Tee-Object -FilePath $log -Append }
function Stop-Requested([string]$before) {
    if (Test-Path $stopFile) { "stopped $(Get-Date -Format o): before $before" | Out-File $status -Encoding utf8; return $true }
    return $false
}

$rebuild = @(
    'ZWO-ASI585MC-Pro/Unidentified-Broadband/Carina-Wide/2025-03-19|ZWO ASI585MC Pro|24mm ASI585 30s 13o 252g 65r 65b',
    'ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/HIP-85088/2025-05-20|ZWO ASI533MC Pro|HIP 85088|Optolong L-Ultimate 3nm',
    'ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/V1045-Ori/2026-01-18|ZWO ASI533MC Pro|V1045 Ori|Optolong L-Ultimate 3nm',
    'ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/eta-Car-Nebula/2026-02-20|ZWO ASI533MC Pro|eta Car Nebula|Optolong L-Ultimate 3nm',
    'SVBONY-SV605CC/Optolong-L-Quad-Enhance/Horsehead-Nebula/2025-10-28|SVBONY SV605CC|Horsehead Nebula|Optolong L-Quad Enhance',
    'SVBONY-SV605CC/Optolong-L-Quad-Enhance/Skull-and-Crossbones-Nebula/2026-02-14|SVBONY SV605CC|Skull and Crossbones Nebula|Optolong L-Quad Enhance')
# A retained master is the session id with '/' and '|' made '_'.
function MasterName([string]$sid) { ($sid -replace '[/|]', '_') + '.fits' }

Set-Status 'starting'
try {
    # 1. The old-domain scoring must be finished: it may call the Release CLI this chain rebuilds.
    $old = Join-Path $LogDir 'e14score-old.status'
    while ((Test-Path $old) -and ((Get-Content $old -Raw).Trim() -like 'running*')) { Set-Status 'waiting for the old-domain scoring'; Start-Sleep 30 }
    Say "old-domain scoring: $((Get-Content $old -Raw).Trim())"
    if (Stop-Requested 'build') { return }

    # 2. Release CLI at HEAD.
    Set-Status 'build Release CLI'
    & dotnet build (Join-Path $repo 'src\TianWen.Cli') -c Release *> (Join-Path $LogDir 'e14evalrf-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed (exit $LASTEXITCODE); see e14evalrf-build.log" }
    Say "built Release at $(git -C $repo rev-parse --short HEAD)"

    # 3. Old masters aside, provenance backed up, then the bake.
    $aside = Join-Path $Scratch 'evalrf-old-masters'
    New-Item -ItemType Directory -Force $aside | Out-Null
    foreach ($sid in $rebuild) { Copy-Item (Join-Path $Bake "session-masters\$(MasterName $sid)") $aside -Force }
    Copy-Item (Join-Path $Bake 'bake-provenance.json') (Join-Path $Bake 'bake-provenance.2026-09-27-lagoon-dark.json') -Force
    if (Stop-Requested 'bake') { return }
    Set-Status 'bake: restack six eval sessions'
    $extra = @('--resume', '--site=-37.877,145.1775', '--warp-interpolation', 'Lanczos3Clamped')
    foreach ($sid in $rebuild) { $extra += '--rebuild-session'; $extra += $sid }
    & (Join-Path $repo 'tools\run-dataset-bake.ps1') -SkipBuild -Out $Bake `
        -ArchiveRoot 'D:\Astro-Organized\lights', 'D:\Astro-Organized\flats', 'D:\Astro-Organized\calibration', 'D:\Astro-Unsorted' `
        -ScratchRoot 'C:\temp\astro-scratch' -ExtraArgs $extra *>> $log
    $prov = Get-Content (Join-Path $Bake 'bake-provenance.json') -Raw | ConvertFrom-Json
    Say "bake pid $($prov.pid), stdout $($prov.stdout)"
    while (Get-Process -Id $prov.pid -ErrorAction SilentlyContinue) { Start-Sleep 30 }
    $tail = Get-Content $prov.stdout | Select-String '^\[dataset\] done:' | Select-Object -Last 1
    Say "bake: $tail"
    if (-not $tail -or $tail.Line -notmatch '\(0 failed') { throw "the bake did not finish cleanly: $tail" }

    # 4. Masters compared; a changed one loses its cached solve.
    $cmp = & python masters_equal.py @($rebuild | ForEach-Object { (Join-Path $aside (MasterName $_)); (Join-Path $Bake "session-masters\$(MasterName $_)") })
    if ($LASTEXITCODE -ne 0) { throw "the master comparison failed (exit $LASTEXITCODE)" }
    $cmp | ForEach-Object { Say "master: $_" }
    $solved = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
    foreach ($line in $cmp) {
        if ($line -like 'DIFF*') {
            $name = [IO.Path]::GetFileNameWithoutExtension(($line -split ' ', 4)[3])
            Get-ChildItem $solved -Filter "$name.*" -ErrorAction SilentlyContinue | Remove-Item
            Say "removed the cached solve of $name"
        }
    }

    # 5. The three ring-fixed caches.
    foreach ($c in @(@{ Name = 'n2n-bb-eval4-rf'; Train = 'arms\bb-eval-train-2.txt'; Val = 'arms\bb-eval-4.txt'; Cells = 60 },
                     @{ Name = 'n2n-e2-eval4b-rf'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4b-25full.txt'; Cells = 60 },
                     @{ Name = 'n2n-eval4-rf'; Train = 'arms\eval-rf-train-1.txt'; Val = 'arms\eval4-25full.txt'; Cells = 48 })) {
        if (Stop-Requested "prepare $($c.Name)") { return }
        $cache = Join-Path $Scratch $c.Name
        if (Test-Path (Join-Path $cache 'meta.json')) { Say "prepare $($c.Name): present, skipped"; continue }
        Set-Status "prepare $($c.Name)"
        & python n2n_smoke.py --prepare --root $Bake --cache $cache --train-from-list $c.Train --val-from-list $c.Val `
            --cells-per-session 5 --val-cells-per-session $c.Cells *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare $($c.Name) failed (exit $LASTEXITCODE)" }
        Say "prepared $($c.Name)"
    }

    # 6. The rf-domain scoring, in this process so its status is the last word.
    if (Stop-Requested 'rf scoring') { return }
    Set-Status 'rf scoring (see e14score-rf.status)'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-poolrf-score.ps1') -Domain rf *>> $log
    Say "rf scoring: $((Get-Content (Join-Path $LogDir 'e14score-rf.status') -Raw).Trim())"
    "done $(Get-Date -Format o): ring-fixed caches prepared and scored" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
