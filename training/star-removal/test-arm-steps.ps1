# Self-contained check of arm-step.ps1 and arm-driver.ps1 on a STUB arm in a temp folder: stub export and train scripts (the stub
# trainer writes <Name>.pt), a stub config, no GPU, no tianwen build and no owner data. Exits non-zero on the first failed
# assertion.
#   pwsh -NoProfile -File training/star-removal/test-arm-steps.ps1
# What it shows: every step over an arm whose outputs all exist (R2u's shape) exits 0 and starts no child (the stubs count their
# starts, and a score or whole step that did start score-arm.ps1 or score_whole.py would fail on the stub config); export and
# train run their stubs when the output is missing; a config that fails to load writes <tag>-chain.stop once and logs the error,
# and the next pass makes no attempt; the driver is still a pass over the steps (one step per pass, <tag>.done at the end); a train
# step over its -Budget stops the stub trainer's whole tree, leaves the resume file, exits 4 and a second call resumes; and the
# onnx step writes the ONNX and its contract through a stub n2n_export.py, skipping once both exist.
$ErrorActionPreference = 'Stop'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$step = Join-Path $PSScriptRoot 'arm-step.ps1'
$driver = Join-Path $PSScriptRoot 'arm-driver.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "arm-steps-test-$PID"
New-Item -ItemType Directory -Force $root | Out-Null

function Assert([bool]$ok, [string]$what) {
    if (-not $ok) { Write-Host "FAILED: $what"; Write-Host "(the stub arms are kept in $root)"; exit 1 }
    Write-Host "ok: $what"
}
function Call([string]$script, [string[]]$arguments) {
    $out = & $pwsh -NoProfile -File $script @arguments 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}
function Starts([string]$arm, [string]$what) {
    $f = "$arm/starts-$what.txt"
    if (Test-Path $f) { @(Get-Content $f).Count } else { 0 }
}
function FailedLines([string]$arm, [string]$tag) {
    $f = "$arm/runs/$tag-chain.log"
    if (Test-Path $f) { @(Get-Content $f | Where-Object { $_ -match 'FAILED' }).Count } else { 0 }
}

# One stub arm: its folders, its config, and the stub export and train scripts (paths baked in, since the driver starts the export
# script with no arguments). -Outputs 'all' is R2u's shape: nothing is missing.
function New-Arm([string]$tag, [string]$outputs, [switch]$BadConfig) {
    $arm = "$root/$tag"
    foreach ($d in 'runs', 'train', 'cache', 'out', 'reports', 'logs') { New-Item -ItemType Directory -Force "$arm/$d" | Out-Null }
    $name = "${tag}_s0"
    Set-Content "$arm/export.ps1" "Add-Content '$arm/starts-export.txt' 'start'; Set-Content '$arm/export.done' 'done'"
    Set-Content "$arm/train.ps1" "param([string]`$Name, [int]`$CheckpointEvery); Add-Content '$arm/starts-train.txt' `"start `$Name`"; Set-Content `"$arm/train/`$Name.pt`" 'weights'"
    # The frozen run folder's n2n_export.py, as a stub: writes <--name> and its contract into <--out>, and counts its starts.
    New-Item -ItemType Directory -Force "$arm/run" | Out-Null
    Set-Content "$arm/run/n2n_export.py" @"
import sys, os
a = dict(zip(sys.argv[1::2], sys.argv[2::2]))
with open(r'$arm/starts-onnx.txt', 'a') as f: f.write('start\n')
open(os.path.join(a['--out'], a['--name']), 'w').write('onnx')
open(os.path.join(a['--out'], a['--name'].replace('.onnx', '.contract.json')), 'w').write('{}')
"@
    $config = [ordered]@{ out_root = "$arm/out"; arm = [ordered]@{ run = "$arm/run"; reports = "$arm/reports"; logs_root = "$arm/logs" } }
    if ($BadConfig) { Set-Content "$arm/config.json" '{ this is not json' } else { Set-Content "$arm/config.json" ($config | ConvertTo-Json -Depth 4) }
    if ($outputs -eq 'all') {
        Set-Content "$arm/export.done" 'done'
        Set-Content "$arm/train/$name.pt" 'weights'
        New-Item -ItemType Directory -Force "$arm/logs/score-$name" | Out-Null
        Set-Content "$arm/logs/score-$name/$name.onnx" 'onnx'
        Set-Content "$arm/logs/score-$name/$name.contract.json" '{}'
        foreach ($r in "real-star-read-$name.txt", "ring-$name.txt", "m31-nucleus-$name.txt") { Set-Content "$arm/reports/$r" 'read' }
        New-Item -ItemType Directory -Force "$arm/out/$tag" | Out-Null
        Set-Content "$arm/out/$tag/row.txt" 'row'
    }
    $common = @('-Tag', $tag, '-Name', $name, '-TrainDir', "$arm/train", '-Cache', "$arm/cache", '-Config', "$arm/config.json", '-Runs', "$arm/runs",
        '-ExportScript', "$arm/export.ps1", '-ExportDone', "$arm/export.done", '-TrainScript', "$arm/train.ps1", '-TrainArgs', "-Name,$name")
    [pscustomobject]@{ Dir = $arm; Tag = $tag; Name = $name; Args = $common }
}

try {
    # 1. an arm whose outputs all exist: every step skips, exit 0, no child.
    $a = New-Arm 'r2u' 'all'
    foreach ($s in 'export', 'train', 'onnx', 'score', 'whole') {
        $r = Call $step (@('-Step', $s) + $a.Args)
        Assert ($r.Code -eq 0) "step $s over a finished arm exits 0 (exit $($r.Code): $($r.Out.Trim()))"
        Assert ($r.Out -match "arm-step $($s): skipped") "step $s says it skipped"
    }
    Assert ((Starts $a.Dir 'export') -eq 0 -and (Starts $a.Dir 'train') -eq 0 -and (Starts $a.Dir 'onnx') -eq 0) 'no stub script was started over a finished arm'
    Assert (-not (Test-Path "$($a.Dir)/runs/r2u-chain.stop")) 'a finished arm wrote no stop file'
    Assert (-not (Test-Path "$($a.Dir)/logs/score-$($a.Name)/score.log")) 'no score-arm.ps1 or score_whole.py was started over a finished arm'

    # 2. export and train run their stubs when the output is missing, and skip on the call after.
    $b = New-Arm 'r3x' 'none'
    $r = Call $step (@('-Step', 'train') + $b.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step train: done') "train runs its stub when the weights are missing (exit $($r.Code): $($r.Out.Trim()))"
    Assert ((Test-Path "$($b.Dir)/train/$($b.Name).pt") -and (Starts $b.Dir 'train') -eq 1) 'the stub trainer ran once and left the weights'
    Assert ((Get-Content "$($b.Dir)/starts-train.txt") -eq "start $($b.Name)") 'the trainer got -TrainArgs split into its own arguments'
    $r = Call $step (@('-Step', 'train') + $b.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'skipped' -and (Starts $b.Dir 'train') -eq 1) 'a second train call skips and starts nothing'
    $r = Call $step (@('-Step', 'export') + $b.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step export: done') "export runs its stub when ExportDone is missing (exit $($r.Code): $($r.Out.Trim()))"
    Assert ((Test-Path "$($b.Dir)/export.done") -and (Starts $b.Dir 'export') -eq 1) 'the stub exporter ran once and left ExportDone'
    $r = Call $step (@('-Step', 'export') + $b.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'skipped' -and (Starts $b.Dir 'export') -eq 1) 'a second export call skips and starts nothing'

    # 3. a config that fails to load: the score step writes the stop file once, logs the error, and the next call makes no attempt.
    $c = New-Arm 'r3c' 'none' -BadConfig
    $r = Call $step (@('-Step', 'score') + $c.Args)
    Assert ($r.Code -ne 0) 'the score step over a config that does not load exits non-zero'
    Assert (Test-Path "$($c.Dir)/runs/r3c-chain.stop") 'it wrote r3c-chain.stop'
    Assert ((FailedLines $c.Dir 'r3c') -eq 1) 'it logged the failure once'
    $r = Call $step (@('-Step', 'score') + $c.Args)
    Assert ($r.Code -ne 0 -and $r.Out -match 'refused') 'the next call refuses while the stop file exists'
    Assert ((FailedLines $c.Dir 'r3c') -eq 1) 'and logged nothing more'

    # 4. the same through the driver: a pass over a config that does not load writes the stop file once; the next pass does nothing.
    $d = New-Arm 'r3d' 'none' -BadConfig
    $r = Call $driver $d.Args
    Assert ($r.Code -ne 0) 'a driver pass over a config that does not load fails'
    Assert (Test-Path "$($d.Dir)/runs/r3d-chain.stop") 'the driver wrote r3d-chain.stop'
    Assert ((FailedLines $d.Dir 'r3d') -eq 1) 'the driver logged the error once'
    Assert ((Get-Content "$($d.Dir)/runs/r3d-chain.log" -Raw) -match 'FAILED: \S+') 'the log names the error'
    $r = Call $driver $d.Args
    Assert ($r.Code -eq 0) 'the next driver pass ends at once'
    Assert ((FailedLines $d.Dir 'r3d') -eq 1 -and (Starts $d.Dir 'export') -eq 0 -and (Starts $d.Dir 'train') -eq 0) 'and made no attempt'

    # 5. the driver is still a pass over the steps: one step that does work per pass, <tag>.done once every step skips.
    $e = New-Arm 'r3e' 'all'
    Remove-Item "$($e.Dir)/export.done", "$($e.Dir)/train/$($e.Name).pt"
    $r = Call $driver $e.Args
    Assert ($r.Code -eq 0 -and (Starts $e.Dir 'export') -eq 1 -and (Starts $e.Dir 'train') -eq 0) "pass 1 exports and ends (exit $($r.Code): $($r.Out.Trim()))"
    Assert (-not (Test-Path "$($e.Dir)/runs/r3e.done")) 'pass 1 is not done'
    $r = Call $driver $e.Args
    Assert ($r.Code -eq 0 -and (Starts $e.Dir 'export') -eq 1 -and (Starts $e.Dir 'train') -eq 1) 'pass 2 skips the export, trains and ends'
    Assert (Test-Path "$($e.Dir)/train/$($e.Name).pt") 'the stub trainer left the weights'
    Assert (-not (Test-Path "$($e.Dir)/runs/r3e.done")) 'pass 2 is not done'
    $r = Call $driver $e.Args
    Assert ($r.Code -eq 0 -and (Test-Path "$($e.Dir)/runs/r3e.done")) 'pass 3 skips every step and writes r3e.done'
    Assert ((Starts $e.Dir 'export') -eq 1 -and (Starts $e.Dir 'train') -eq 1) 'pass 3 started no stub'
    Assert (-not (Test-Path "$($e.Dir)/runs/r3e-chain.stop")) 'no stop file after a clean arm'

    # 6. a failing stub (the trainer leaves no weights) stops the chain once, through the driver as through a step.
    $f = New-Arm 'r3f' 'none'
    Set-Content "$($f.Dir)/train.ps1" "param([string]`$Name, [int]`$CheckpointEvery); Add-Content '$($f.Dir)/starts-train.txt' 'start'; exit 7"
    Set-Content "$($f.Dir)/export.done" 'done'
    $r = Call $driver $f.Args
    Assert (Test-Path "$($f.Dir)/runs/r3f-chain.stop") 'a trainer that leaves no weights writes the stop file'
    Assert ((FailedLines $f.Dir 'r3f') -eq 1) 'and the failure is logged once'
    $r = Call $driver $f.Args
    Assert ((Starts $f.Dir 'train') -eq 1) 'the next pass does not run the trainer again'

    # 7. the train budget: a stub trainer writes a resume file, starts a child of its own and sleeps. A short -Budget stops the whole
    # tree, leaves the resume file, exits 4 with the documented words and starts no second trainer; a second call resumes (the stub
    # sees the resume file, as the real trainer's --resume does) and finishes with <Name>.pt.
    $g = New-Arm 'r3g' 'none'
    function New-BudgetTrainer([string]$arm) {
        $text = @'
param([string]$Name, [int]$CheckpointEvery)
Add-Content 'ARM/starts-train.txt' "start $Name every $CheckpointEvery"
$resume = 'ARM/train/' + $Name + '.resume.pt'
if (Test-Path $resume) {
    Add-Content 'ARM/starts-train.txt' 'resumed'
    Remove-Item $resume
    Set-Content ('ARM/train/' + $Name + '.pt') 'weights'
    exit 0
}
Set-Content $resume 'checkpoint'
$child = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 600' -PassThru -NoNewWindow
Set-Content 'ARM/child.pid' $child.Id
Start-Sleep 600
'@
        Set-Content "$arm/train.ps1" $text.Replace('ARM', $arm)
    }
    New-BudgetTrainer $g.Dir
    $r = Call $step (@('-Step', 'train', '-Budget', '00:00:06', '-CheckpointEvery', '250') + $g.Args)
    Assert ($r.Code -eq 4) "a train step past its budget exits 4, resumable (exit $($r.Code): $($r.Out.Trim()))"
    Assert ($r.Out -match 'resumable: run the train step again') 'it says resumable: run the train step again'
    Assert (Test-Path "$($g.Dir)/train/$($g.Name).resume.pt") 'the resume file is left'
    Assert (-not (Test-Path "$($g.Dir)/train/$($g.Name).pt")) 'there are no final weights yet'
    Assert ((Starts $g.Dir 'train') -eq 1) 'no second trainer was started'
    Assert ((Get-Content "$($g.Dir)/starts-train.txt") -eq "start $($g.Name) every 250") 'the trainer got -CheckpointEvery'
    $childId = [int](Get-Content "$($g.Dir)/child.pid")
    Assert (-not (Get-Process -Id $childId -ErrorAction SilentlyContinue)) "the trainer's own child process ($childId) was stopped with it"
    Assert (-not (Test-Path "$($g.Dir)/runs/r3g-chain.stop")) 'a budget stop is not a failure and writes no stop file'
    $r = Call $step (@('-Step', 'train', '-Budget', '00:00:30') + $g.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step train: done') "the second call resumes and finishes (exit $($r.Code): $($r.Out.Trim()))"
    Assert ((Get-Content "$($g.Dir)/starts-train.txt") -contains 'resumed') 'the second trainer saw the resume file'
    Assert (Test-Path "$($g.Dir)/train/$($g.Name).pt") 'the weights are there'

    # 8. a budget stop with no resume file is a failure with the stop file: there is nothing to resume from.
    $h = New-Arm 'r3h' 'none'
    Set-Content "$($h.Dir)/train.ps1" "param([string]`$Name, [int]`$CheckpointEvery); Add-Content '$($h.Dir)/starts-train.txt' 'start'; Start-Sleep 600"
    $r = Call $step (@('-Step', 'train', '-Budget', '00:00:03') + $h.Args)
    Assert ($r.Code -ne 0 -and $r.Code -ne 4 -and (Test-Path "$($h.Dir)/runs/r3h-chain.stop")) 'a budget stop that left no resume file fails and writes the stop file'

    # 8b. a trainer that had already written <Name>.pt (n2n_smoke removes the resume file right after its final save; run-r2.ps1 then goes
    # on to its next seed or arm) and is stopped by the budget has finished: done, exit 0, no stop file.
    $k = New-Arm 'r3k' 'none'
    Set-Content "$($k.Dir)/train.ps1" "param([string]`$Name, [int]`$CheckpointEvery); Add-Content '$($k.Dir)/starts-train.txt' 'start'; Set-Content '$($k.Dir)/train/$($k.Name).pt' 'weights'; Start-Sleep 600"
    $r = Call $step (@('-Step', 'train', '-Budget', '00:00:06') + $k.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step train: done') "a budget stop after the weights were written is done (exit $($r.Code): $($r.Out.Trim()))"
    Assert (-not (Test-Path "$($k.Dir)/runs/r3k-chain.stop")) 'and writes no stop file'
    Assert (-not (Test-Path "$($k.Dir)/train/$($k.Name).resume.pt")) 'and there is no resume file'

    # 9. the onnx step writes <Name>.onnx and its contract from the weights, and skips once both exist.
    $i = New-Arm 'r3i' 'none'
    Set-Content "$($i.Dir)/train/$($i.Name).pt" 'weights'
    $r = Call $step (@('-Step', 'onnx') + $i.Args)
    $score = "$($i.Dir)/logs/score-$($i.Name)"
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step onnx: done') "the onnx step exports (exit $($r.Code): $($r.Out.Trim()))"
    Assert ((Test-Path "$score/$($i.Name).onnx") -and (Test-Path "$score/$($i.Name).contract.json") -and (Test-Path "$($i.Dir)/cache/$($i.Name).pt")) 'it left the ONNX and its contract in the score folder, from the weights in the cache'
    $r = Call $step (@('-Step', 'onnx') + $i.Args)
    Assert ($r.Code -eq 0 -and $r.Out -match 'arm-step onnx: skipped' -and (Starts $i.Dir 'onnx') -eq 1) 'a second onnx call skips and starts no export'
    Remove-Item "$score/$($i.Name).contract.json"
    $r = Call $step (@('-Step', 'onnx') + $i.Args)
    Assert ($r.Code -eq 0 -and (Starts $i.Dir 'onnx') -eq 2) 'an ONNX without its contract is exported again'

    # 10. the driver treats a budget stop as neither done nor failed: no stop file, and the next pass resumes.
    $j = New-Arm 'r3j' 'none'
    Set-Content "$($j.Dir)/export.done" 'done'
    New-BudgetTrainer $j.Dir
    $r = Call $driver (@('-Budget', '00:00:06') + $j.Args)
    Assert ($r.Code -eq 0 -and -not (Test-Path "$($j.Dir)/runs/r3j-chain.stop") -and (Test-Path "$($j.Dir)/train/$($j.Name).resume.pt")) "a driver pass that hits the budget ends with the checkpoint and no stop file (exit $($r.Code): $($r.Out.Trim()))"
    $r = Call $driver (@('-Budget', '00:00:30') + $j.Args)
    Assert ((Test-Path "$($j.Dir)/train/$($j.Name).pt") -and (Starts $j.Dir 'train') -ge 2) 'the next pass resumes and finishes the training'

    Write-Host 'all arm-step checks passed'
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
catch {
    Write-Host "FAILED: $_"
    Write-Host "(the stub arms are kept in $root)"
    exit 1
}
