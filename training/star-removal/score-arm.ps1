# One star-remover arm's scorecard, run by az-driver.ps1 as its -AfterScript once the weights are home and the VM is gone
# (training/azure/README.md), or by arm-driver.ps1 after a local training run: A's draws and R2k's twins at one view, the real
# tiles' check against the earlier arms, the ring, and M31's nucleus over 8 views. RESUMABLE: every step whose output exists is
# skipped, so a second run over a scored arm does no inference and touches no GPU, and exits 0.
#   pwsh -File score-arm.ps1 -Name r2u_synth4_s0 -Weights <cache>/r2u_synth4_s0.pt
# The machine's paths are not here. They come from the "arm" object of scorecard.local.json (scorecard.example.json shows the
# keys), or the file -Config names. A second set of A's draws and twins is a second config with NEW random_export, twins_root,
# eval_root, reports and logs_root: the skips below key on those folders, so a shared one would read as already scored.
# -Baselines are the models the real-star check and M31's nucleus compare the arm with.
param(
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string]$Weights,
    [string]$Config = (Join-Path $PSScriptRoot 'scorecard.local.json'),
    # The frozen run directory whose r2_infer.py and n2n_export.py read the weights (a copy of training/denoise at the arm's commit).
    [string]$Run = '',
    # Output folders (under the config's eval_root) the real-star check lists beside the arm; 'input' is the noisy tile itself.
    [string[]]$RealStarBaselines = @(),
    # The ring check's baseline output folders.
    [string[]]$RingBaselines = @(),
    # M31's starless plate of the model the nucleus read is compared with (a v8 plate of an earlier arm).
    [string]$NucleusReference = '',
    # Steps to leave out: 'ring', 'nucleus'. A re-score on a second set of draws (F2's) has no use for M31's nucleus, which does not
    # depend on the draws, and a job's -File arrives with an array as one comma-joined string, split below.
    [string[]]$Skip = @()
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Config)) { throw "no scorecard settings at $Config (scorecard.example.json shows the shape)" }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$arm = $cfg.arm
if ($null -eq $arm) { throw "$Config has no 'arm' object (scorecard.example.json)" }
foreach ($key in 'run', 'eval_cli', 'remove_cli', 'eval_root', 'reports', 'logs_root', 'real_export', 'random_export', 'twins_root', 'val_list', 'plates_root', 'm31_master', 'm31_out') {
    if ($null -eq $arm.$key -or "$($arm.$key)" -eq '') { throw "$Config does not name arm.$key" }
}
if (-not $Run) { $Run = $arm.run }
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $RealStarBaselines.Count -and $arm.real_star_baselines) { $RealStarBaselines = @($arm.real_star_baselines) }
# A baseline is a model already scored over the real tiles, and never the arm itself: one not yet scored is left out of this arm's
# read (the real-star check cannot open its outputs), and a list that names the arm does not read it twice.
$RealStarBaselines = @($RealStarBaselines | ForEach-Object { $_ -split ',' } | Where-Object { $_ -and $_ -ne $Name -and ($_ -eq 'input' -or (Test-Path "$($arm.eval_root)/$_/on-real-val/outputs.jsonl")) })
if (-not $RingBaselines.Count -and $arm.ring_baselines) { $RingBaselines = @($arm.ring_baselines) }
if (-not $NucleusReference -and $arm.nucleus_reference) { $NucleusReference = $arm.nucleus_reference }

$cache = Split-Path $Weights -Parent
if ((Split-Path $Weights -Leaf) -ne "$Name.pt") { throw "-Weights must be <cache>/$Name.pt" }
$logs = "$($arm.logs_root)/score-$Name"
$evals = "$($arm.eval_root)/$Name"
New-Item -ItemType Directory -Force $logs | Out-Null
New-Item -ItemType Directory -Force $arm.reports | Out-Null
function Say([string]$what) { "$(Get-Date -Format o) $what" | Tee-Object -FilePath "$logs/score.log" -Append }
function Py([string[]]$arguments, [string]$what, [string]$dir = $Run) {
    $p = Start-Process python -ArgumentList $arguments -WorkingDirectory $dir -NoNewWindow -PassThru -Wait `
        -RedirectStandardOutput "$logs/$what.log" -RedirectStandardError "$logs/$what.err.log"
    if ($p.ExitCode -ne 0) { throw "$what exited $($p.ExitCode); see $logs/$what.err.log" }
}

$jobs = @(, @($arm.random_export, 'on-random'))
foreach ($twin in 'plain', 'lobed') { $jobs += , @("$($arm.twins_root)/$twin", "on-twin-$twin") }
foreach ($j in $jobs) {
    $out = "$evals/$($j[1])"
    if (Test-Path "$out/starless-eval-v5.json") { continue }
    if (-not (Test-Path "$out/outputs.jsonl")) {
        Say "scoring: $Name over $($j[1])"
        Py @('r2_infer.py', '--export', $j[0], '--cache', $cache, '--ckpt', "$Name.pt", '--tta', '1', '--val-from-list', $arm.val_list, '--out', $out) "infer-$($j[1])"
    }
    Say "scoring: the eval of $Name over $($j[1])"
    $e = Start-Process $arm.eval_cli -ArgumentList 'dataset', 'starless-eval', '--export', $j[0], '--outputs', $out, '--report-name', 'starless-eval-v5.json' `
        -NoNewWindow -PassThru -Wait -RedirectStandardOutput "$logs/eval-$($j[1]).log" -RedirectStandardError "$logs/eval-$($j[1]).err.log"
    if ($e.ExitCode -ne 0) { throw "the eval over $($j[1]) exited $($e.ExitCode)" }
}
$real = "$evals/on-real-val"
if (-not (Test-Path "$real/outputs.jsonl")) {
    Say "scoring: $Name over the real tiles"
    Py @('r2_infer.py', '--export', $arm.real_export, '--cache', $cache, '--ckpt', "$Name.pt", '--val-from-list', $arm.val_list, '--out', $real) 'infer-real'
}
$realRead = "$($arm.reports)/real-star-read-$Name.txt"
if (-not (Test-Path $realRead)) {
    Say 'measuring: the real-star check'
    $check = @('-I', "$PSScriptRoot/real_star_check.py", '--export', $arm.real_export, '--plates', $arm.plates_root, '--eval-root', $arm.eval_root, 'input') + @($RealStarBaselines | Select-Object -First 1) + $Name + @($RealStarBaselines | Select-Object -Skip 1)
    Py $check 'realstar' $logs
    Copy-Item "$logs/realstar.log" $realRead -Force
}
$ringRead = "$($arm.reports)/ring-$Name.txt"
if ($Skip -notcontains 'ring' -and -not (Test-Path $ringRead)) {
    Say 'measuring: the ring'
    $ring = @('-I', "$PSScriptRoot/ring_annulus.py", '--export', $arm.random_export, '--eval-root', $arm.eval_root) + $RingBaselines + $Name
    Py $ring 'ring' $logs
    Copy-Item "$logs/ring.log" $ringRead -Force
}
$nucleusRead = "$($arm.reports)/m31-nucleus-$Name.txt"
if ($Skip -notcontains 'nucleus' -and -not (Test-Path $nucleusRead)) {
    $onnx = "$logs/$Name.onnx"
    # arm-step.ps1 -Step onnx writes this file (and its contract) ahead of the score; it is reused, never exported twice.
    if (-not (Test-Path $onnx)) {
        Say "exporting $Name to ONNX"
        Py @('n2n_export.py', '--cache', $cache, '--ckpt', "$Name.pt", '--eval', $cache, '--out', $logs, '--name', "$Name.onnx") 'export'
    }
    $m31out = "$($arm.m31_out)/m31_${Name}_v8.fits"
    if (-not (Test-Path $m31out)) {
        Say "M31 through $Name, 8 views"
        New-Item -ItemType Directory -Force $arm.m31_out | Out-Null
        $w = Start-Process $arm.remove_cli -ArgumentList 'image', 'remove-stars', $arm.m31_master, '-o', $m31out,
            '--no-stars', '--model', $onnx, '--views', '8' -NoNewWindow -PassThru -Wait `
            -RedirectStandardOutput "$logs/m31.log" -RedirectStandardError "$logs/m31.err.log"
        if ($w.ExitCode -ne 0) { throw "remove-stars over M31 exited $($w.ExitCode)" }
    }
    $nucleus = @('-I', "$PSScriptRoot/m31_nucleus.py", $arm.m31_master) + @($NucleusReference | Where-Object { $_ }) + $m31out
    Py $nucleus 'nucleus' $logs
    Copy-Item "$logs/nucleus.log" $nucleusRead -Force
}
Say "done: real-star-read-$Name.txt$(if ($Skip -notcontains 'ring') { ", ring-$Name.txt" })$(if ($Skip -notcontains 'nucleus') { " and m31-nucleus-$Name.txt" }) written"
