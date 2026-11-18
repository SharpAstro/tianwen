# The two held-beta exports behind item 5's "drawn, --star-beta 8" and "--star-beta 20" rows (R3w, 2026-11-16): dig-2x2.ps1's
# plate-plain arm (the real starless plate, no wing draw, everything else synth22's) with ONE option added, --star-beta N,
# every star at one beta N. Same binary and eight validation sessions as dig-2x2.ps1; outputs plate-beta8 and plate-beta20
# beside its arms. Read with flank_cmp.py <session key> <root>.
# Paths: TIANWEN_SR_SCRATCH (default D:/temp/tianwen-scratch), TIANWEN_DATASET (default D:/Astro-Dataset/2026-10-12-full),
# TIANWEN_CLI (default <scratch>/bh-bin8/tianwen.exe).
$ErrorActionPreference = 'Continue'
$scratch = if ($env:TIANWEN_SR_SCRATCH) { $env:TIANWEN_SR_SCRATCH } else { 'D:/temp/tianwen-scratch' }
$store = if ($env:TIANWEN_DATASET) { $env:TIANWEN_DATASET } else { 'D:/Astro-Dataset/2026-10-12-full' }
$bin = if ($env:TIANWEN_CLI) { $env:TIANWEN_CLI } else { "$scratch/bh-bin8/tianwen.exe" }
$out = "$scratch/dig-2x2"
New-Item -ItemType Directory -Force $out | Out-Null
$sessions = (python -I -c "import json,sys;print('\n'.join(json.load(open(sys.argv[1]))['val_sessions']))" "$scratch/r2-synth22/meta.json") -split "`n" | Where-Object { $_ }
$base = @('dataset', 'degrade', '--bake', "`"$store`"", '--plates', "`"$store/starless`"", '--mode', 'stars', '--shape', 'warped',
    '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1', '--stretch-from', 'master',
    '--background', 'plate')
foreach ($s in $sessions) { $base += '--session'; $base += "`"$($s.Trim())`"" }
foreach ($beta in 8, 20) {
    $n = "plate-beta$beta"
    if (Test-Path "$out/$n/injections.jsonl") { continue }
    $p = Start-Process -FilePath $bin -ArgumentList ($base + @('--star-beta', "$beta", '--out', "`"$out/$n`"")) `
        -RedirectStandardOutput "$out/$n.log" -RedirectStandardError "$out/$n.err.log" -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    "$n exit $($p.ExitCode)"
}
