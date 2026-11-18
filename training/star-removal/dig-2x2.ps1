# The 2x2 that says where the dig comes from (2026-11-16, after R3w): the eight validation sessions exported four ways
# from one binary (bh-bin8), background synthetic or the real starless plate, the drawn wings (--star-beta-draw) on or
# off, everything else synth22's; then R3v's seed 0 run on each and its truth error under removed stars read
# (export_dig.py). All on D:.
$ErrorActionPreference = 'Continue'
$bin = 'D:/temp/tianwen-scratch/bh-bin8/tianwen.exe'
$store = 'D:/Astro-Dataset/2026-10-12-full'
$out = 'D:/temp/tianwen-scratch/dig-2x2'
$log = 'C:/temp/e2/explore/dig-2x2.log'
New-Item -ItemType Directory -Force $out | Out-Null
$sessions = (python -c "import json;print('\n'.join(json.load(open('D:/temp/tianwen-scratch/r2-synth22/meta.json'))['val_sessions']))") -split "`n" | Where-Object { $_ }
$base = @('dataset', 'degrade', '--bake', "`"$store`"", '--plates', "`"$store/starless`"", '--mode', 'stars', '--shape', 'warped',
    '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1', '--stretch-from', 'master')
foreach ($s in $sessions) { $base += '--session'; $base += "`"$($s.Trim())`"" }
$synthetic = @('--background', 'synthetic', '--steer', '0.5,2,0.8,1', '--nebula-knots', '--bright-knots-wide')
$beta = @('--star-beta-draw', '0.3,1.3,6,3')
$arms = @(
    @{ n = 'synth-wings'; x = $synthetic + $beta }, @{ n = 'synth-plain'; x = $synthetic },
    @{ n = 'plate-wings'; x = @('--background', 'plate') + $beta }, @{ n = 'plate-plain'; x = @('--background', 'plate') })
foreach ($arm in $arms) {
    $n = $arm.n
    if (Test-Path "$out/$n/injections.jsonl") { continue }
    "$(Get-Date -Format o) export $n" | Out-File $log -Append
    $p = Start-Process -FilePath $bin -ArgumentList ($base + $arm.x + @('--out', "`"$out/$n`"")) -RedirectStandardOutput "$out/$n.log" -RedirectStandardError "$out/$n.err.log" -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    "$(Get-Date -Format o) export $n exit $($p.ExitCode)" | Out-File $log -Append
}
$roots = $arms | ForEach-Object { "$out/$($_.n)" } | Where-Object { Test-Path "$_/injections.jsonl" }
foreach ($w in 'r3v_synth22_s0.pt', 'r3v_synth22_s1.pt') {
    $r = Start-Process python -ArgumentList (@('-I', 'C:/temp/e2/explore/export_dig.py', 'D:/temp/tianwen-scratch/r2-synth22', $w) + $roots) `
        -WorkingDirectory 'C:/temp/e2/r3h-run' -NoNewWindow -PassThru -Wait -RedirectStandardOutput "C:/temp/e2/explore/dig-2x2-$w.txt" -RedirectStandardError "C:/temp/e2/explore/dig-2x2-$w.err"
    "$(Get-Date -Format o) read $w exit $($r.ExitCode)" | Out-File $log -Append
}
'done' | Out-File 'C:/temp/e2/explore/dig-2x2.done'
