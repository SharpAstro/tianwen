# Self-contained check of arm-step.ps1 and arm-driver.ps1 on a STUB arm in a temp folder: stub export and train scripts (the stub
# trainer writes <Name>.pt), a stub config, no GPU, no tianwen build and no owner data. Exits non-zero on the first failed
# assertion.
#   pwsh -NoProfile -File training/star-removal/test-arm-steps.ps1
# What it shows: every step over an arm whose outputs all exist (R2u's shape) exits 0 and starts no child (the stubs count their
# starts, and a score or whole step that did start score-arm.ps1 or score_whole.py would fail on the stub config); export and
# train run their stubs when the output is missing; a config that fails to load writes <tag>-chain.stop once and logs the error,
# and the next pass makes no attempt; and the driver is still a pass over the steps (one step per pass, <tag>.done at the end).
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
    Set-Content "$arm/train.ps1" "param([string]`$Name); Add-Content '$arm/starts-train.txt' `"start `$Name`"; Set-Content `"$arm/train/`$Name.pt`" 'weights'"
    $config = [ordered]@{ out_root = "$arm/out"; arm = [ordered]@{ reports = "$arm/reports"; logs_root = "$arm/logs" } }
    if ($BadConfig) { Set-Content "$arm/config.json" '{ this is not json' } else { Set-Content "$arm/config.json" ($config | ConvertTo-Json -Depth 4) }
    if ($outputs -eq 'all') {
        Set-Content "$arm/export.done" 'done'
        Set-Content "$arm/train/$name.pt" 'weights'
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
    foreach ($s in 'export', 'train', 'score', 'whole') {
        $r = Call $step (@('-Step', $s) + $a.Args)
        Assert ($r.Code -eq 0) "step $s over a finished arm exits 0 (exit $($r.Code): $($r.Out.Trim()))"
        Assert ($r.Out -match "arm-step $($s): skipped") "step $s says it skipped"
    }
    Assert ((Starts $a.Dir 'export') -eq 0 -and (Starts $a.Dir 'train') -eq 0) 'no stub script was started over a finished arm'
    Assert (-not (Test-Path "$($a.Dir)/runs/r2u-chain.stop")) 'a finished arm wrote no stop file'
    Assert (-not (Test-Path "$($a.Dir)/logs/score-$($a.Name)")) 'no score-arm.ps1 or score_whole.py was started over a finished arm'

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
    Set-Content "$($f.Dir)/train.ps1" "param([string]`$Name); Add-Content '$($f.Dir)/starts-train.txt' 'start'; exit 7"
    Set-Content "$($f.Dir)/export.done" 'done'
    $r = Call $driver $f.Args
    Assert (Test-Path "$($f.Dir)/runs/r3f-chain.stop") 'a trainer that leaves no weights writes the stop file'
    Assert ((FailedLines $f.Dir 'r3f') -eq 1) 'and the failure is logged once'
    $r = Call $driver $f.Args
    Assert ((Starts $f.Dir 'train') -eq 1) 'the next pass does not run the trainer again'

    Write-Host 'all arm-step checks passed'
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
catch {
    Write-Host "FAILED: $_"
    Write-Host "(the stub arms are kept in $root)"
    exit 1
}
