# F2's re-export (#1827): A's draws and both of R2k's twins on the master's stretch, on DE1.
#   pilot run --node <de1> --timeout 8h -- pwsh -NoProfile -File training/star-removal/f2-export.ps1 -Cli <tianwen.exe> -Commit <sha> -Root C:/temp/tianwen-scratch
#
# The recipe is restore-r1-train-random-val.ps1's (A's draws) and r2k-twins.ps1's (the twins), argument for argument: the
# 2026-09-29 store and its plates, the eval's 8 validation sessions (C:/temp/e2/r1-train-random-val-sessions.txt), --seed 1,
# 40 cells, 4 draws, --shape warped --warp-sigma 0.5 --warp-sigma-drizzle 0. The one change is --stretch-from master, stated
# here although it is the default. random and plain are the same command; the plain twin only exists so the lobed one has a
# partner of its own, and its rows equal A's but for the Lobe field. Resumable: a set with its .done file is left alone.
param(
    [Parameter(Mandatory)] [string]$Cli,
    [Parameter(Mandatory)] [string]$Commit,
    [string]$Root = 'C:/temp/tianwen-scratch',
    [string]$Bake = 'D:/Astro-Dataset/2026-09-29-full',
    [string]$SessionsFile = 'C:/temp/e2/r1-train-random-val-sessions.txt'
)
$ErrorActionPreference = 'Stop'
if ($Root -match '^[eE]:' -or $Bake -match '^[eE]:') { throw 'never E:' }
$plates = "$Bake/starless"
$sessions = @(Get-Content $SessionsFile | Where-Object { $_ })
$version = (& $Cli --version 2>&1 | Select-Object -First 1)
$sets = [ordered]@{ 'f2-random-master' = @(); 'f2-twin-plain-master' = @(); 'f2-twin-lobed-master' = @('--star-lobes') }
New-Item -ItemType Directory -Force $Root | Out-Null

foreach ($name in $sets.Keys) {
    $out = "$Root/$name"
    $log = "$Root/$name.log"
    if (Test-Path "$out/.done") { "$(Get-Date -Format o) $name already done"; continue }
    $a = @('dataset', 'degrade', '--bake', "`"$Bake`"", '--plates', "`"$plates`"", '--mode', 'stars', '--shape', 'warped',
        '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1',
        '--stretch-from', 'master') + $sets[$name] + @('--out', "`"$out`"")
    foreach ($s in $sessions) { $a += '--session'; $a += "`"$s`"" }
    New-Item -ItemType Directory -Force $out | Out-Null
    $record = @("tianwen commit $Commit (flight/tia1827), $version", "exported $(Get-Date -Format o) on $env:COMPUTERNAME",
        "$Cli $($a -join ' ')", "sessions file $SessionsFile ($($sessions.Count) sessions)")
    $record | Out-File "$out/FROZEN-AT.txt" -Encoding utf8
    "$(Get-Date -Format o) $name start"
    $p = Start-Process -FilePath $Cli -ArgumentList $a -RedirectStandardOutput $log -RedirectStandardError "$Root/$name.err.log" -NoNewWindow -PassThru -Wait
    "$(Get-Date -Format o) $name exit $($p.ExitCode)"
    if ($p.ExitCode -ne 0) { throw "$name exited $($p.ExitCode); see $log" }
    New-Item -ItemType File "$out/.done" | Out-Null
}
'f2-export: done'
