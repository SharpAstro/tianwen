# R3y's Q1 export (docs/plans/star-remover-training.md, "R3y, pre-registered"): one arm, synth-draw12, the registered
# synth26 line (--background synthetic and synth22's recipe with --star-beta-draw 0.5,1.3,12,3) on the eight validation
# sessions, from the binary the training export uses (bh-bin8), for `flank_cmp.py <session key> <export> --min-beta 8`.
# DE1 paths, all on D:.
$ErrorActionPreference = 'Continue'
$bin = 'D:/temp/tianwen-scratch/bh-bin8/tianwen.exe'
$store = 'D:/Astro-Dataset/2026-10-12-full'
$out = 'D:/temp/tianwen-scratch/dig-2x2'
$log = 'C:/temp/e2/explore/q1-draw12.log'
New-Item -ItemType Directory -Force $out | Out-Null
$sessions = (python -c "import json;print('\n'.join(json.load(open('D:/temp/tianwen-scratch/r2-synth22/meta.json'))['val_sessions']))") -split "`n" | Where-Object { $_ }
$base = @('dataset', 'degrade', '--bake', "`"$store`"", '--plates', "`"$store/starless`"", '--mode', 'stars', '--shape', 'warped',
    '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1', '--stretch-from', 'master')
foreach ($s in $sessions) { $base += '--session'; $base += "`"$($s.Trim())`"" }
$arms = @(
    @{ n = 'synth-draw12'; x = @('--background', 'synthetic', '--steer', '0.5,2,0.8,1', '--nebula-knots', '--bright-knots-wide', '--star-beta-draw', '0.5,1.3,12,3') })
foreach ($arm in $arms) {
    $n = $arm.n
    if (Test-Path "$out/$n/injections.jsonl") { continue }
    "$(Get-Date -Format o) export $n" | Out-File $log -Append
    $p = Start-Process -FilePath $bin -ArgumentList ($base + $arm.x + @('--out', "`"$out/$n`"")) -RedirectStandardOutput "$out/$n.log" -RedirectStandardError "$out/$n.err.log" -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    "$(Get-Date -Format o) export $n exit $($p.ExitCode)" | Out-File $log -Append
}
'done' | Out-File 'C:/temp/e2/explore/q1-draw12.done'
