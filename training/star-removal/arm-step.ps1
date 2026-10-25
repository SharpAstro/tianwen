# One STEP of a star-remover arm trained on THIS machine, run alone: as a pilot job (the arm as a flight, docs/plans/star-remover-training.md)
#   pilot run -- pwsh -NoProfile -File arm-step.ps1 -Step export|train|onnx|score|whole -Tag r3a -Name r3a_s0 -TrainDir ... -Cache ... -Runs ...
# or by arm-driver.ps1, which is a pass over these five steps, so the scheduled task and a pilot job run the same code.
#   export  runs -ExportScript (itself resumable); its output is -ExportDone
#   train   runs -TrainScript -TrainArgs; its output is <TrainDir>/<Name>.pt (see the budget below)
#   onnx    runs n2n_export.py from the config's arm.run over <Cache>/<Name>.pt; its output is <Name>.onnx and <Name>.contract.json in
#           the score folder (<arm.logs_root>/score-<Name>), which score-arm.ps1 and the whole step read
#   score   copies the weights into -Cache and runs score-arm.ps1; its output is the three reports in the config's arm.reports
#   whole   runs score_whole.py over the ONNX score left; its output is <out root>/<WholeTag>/row.txt
# A step whose output exists is SKIPPED with exit 0 and starts no child. Every call prints one line saying what it did or skipped.
# A failure exits non-zero AND writes <tag>-chain.stop in the runs folder, as the driver's Halt does, so a deterministic failure is
# tried once and not every 10 minutes for a night (#1787); a step also refuses to run while that file exists. The one exit that is
# neither success nor failure is 3: the train step found another trainer holding the GPU and did nothing, so the stop file is not
# written and a later call may go on. The other is 4, "resumable": the train step reached its -Budget, stopped the trainer's whole
# process tree and left the trainer's resume checkpoint (<TrainDir>/<Name>.resume.pt) whole, so run the train step again and it
# continues. -Runs, or TIANWEN_RUNS, names the runs folder; this file names no machine path.
# THE TRAIN BUDGET. pilot run ends a job at 8 h and a training can take longer, so the train step passes -CheckpointEvery N to the
# train script (run-r2.ps1 forwards it as --checkpoint-every plus --resume; 0 passes nothing) and waits on the trainer for at most
# -Budget (default 7h30m). At the budget it stops the trainer's process tree itself, ahead of the 8 h kill: the trainer replaces its
# resume file by an atomic rename, so the last checkpoint is whole whenever the process dies. It says 'resumable: run the train step
# again' and exits 4; the next call starts the trainer again, which resumes from that file. A trainer that exits without <Name>.pt is
# still a failure, as is a budget stop that left no resume file (nothing to resume from).
# Under -File an array parameter arrives as ONE comma-joined string, so -TrainArgs is split on commas here.
param(
    [Parameter(Mandatory)] [ValidateSet('export', 'train', 'onnx', 'score', 'whole')] [string]$Step,
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string]$TrainDir,
    [Parameter(Mandatory)] [string]$Cache,
    [string]$Config = (Join-Path $PSScriptRoot 'scorecard.local.json'),
    [string]$Runs = $env:TIANWEN_RUNS,
    [string]$GpuTrainerPattern = 'n2n_smoke\.py --train',
    [string]$ExportScript = '',
    [string]$ExportDone = '',
    [string]$TrainScript = '',
    [string[]]$TrainArgs = @(),
    # The longest the train step lets the trainer run before it stops it for a later call to resume; keep it under pilot run's 8 h.
    [timespan]$Budget = [timespan]'07:30:00',
    # Passed to the train script as -CheckpointEvery (steps between resume checkpoints); 0 passes nothing.
    [int]$CheckpointEvery = 500,
    # The tag score_whole.py writes the row under, when it is not -Tag.
    [string]$WholeTag = ''
)
$ErrorActionPreference = 'Stop'
if (-not $WholeTag) { $WholeTag = $Tag }
if (-not $Runs) { throw 'no runs folder: pass -Runs or set TIANWEN_RUNS' }
$TrainArgs = @($TrainArgs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$status = "$Runs/$Tag-chain.status"
$log = "$Runs/$Tag-chain.log"
$stop = "$Runs/$Tag-chain.stop"
function Say([string]$what) { "$(Get-Date -Format o) $what" | Out-File $log -Append -Encoding utf8; "$what $(Get-Date -Format o)" | Out-File $status -Encoding utf8 }
function Halt([string]$why) { Say "FAILED: $why"; New-Item -ItemType File $stop -Force | Out-Null }
# A failure the step names itself: the catch below writes the stop file for it, once, and the exit code is non-zero.
function Fail([string]$why) { throw $why }
function Skipped([string]$why) { Say "$Step skipped: $why"; "arm-step $($Step): skipped ($why)" }
# The ids of a process and everything it started, root first.
function ProcessTree([int]$root) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = [System.Collections.Generic.List[int]]::new()
    $ids.Add($root)
    for ($i = 0; $i -lt $ids.Count; $i++) {
        foreach ($c in $all | Where-Object { $_.ParentProcessId -eq $ids[$i] }) { if (-not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } }
    }
    $ids
}
function Child([string]$program, [string[]]$arguments) {
    Start-Process $program -ArgumentList $arguments -NoNewWindow -PassThru -Wait
}

New-Item -ItemType Directory -Force $Runs | Out-Null
if (Test-Path $stop) { Say "$Step refused: $stop exists"; "arm-step $($Step): refused ($stop exists)"; exit 2 }
try {
    switch ($Step) {
        'export' {
            if (-not $ExportScript -or -not $ExportDone) { Skipped 'no -ExportScript and -ExportDone'; break }
            if (Test-Path $ExportDone) { Skipped "$ExportDone exists"; break }
            Say "exporting ($(Split-Path $ExportScript -Leaf), resumable)"
            $x = Child 'pwsh' @('-NoProfile', '-File', $ExportScript)
            if (-not (Test-Path $ExportDone)) { Fail "the export ended (exit $($x.ExitCode)) without $ExportDone" }
            Say 'exported'
            "arm-step export: done ($ExportDone)"
        }
        'train' {
            if (Test-Path "$TrainDir/$Name.pt") { Skipped "$TrainDir/$Name.pt exists"; break }
            if (-not $TrainScript) { Fail "no $TrainDir/$Name.pt and no -TrainScript" }
            $busy = @(Get-CimInstance Win32_Process | Where-Object {
                $_.CommandLine -and @('python.exe', 'python3.exe', 'py.exe') -contains $_.Name -and $_.CommandLine -match $GpuTrainerPattern
            }).Count -gt 0
            if ($busy) { Say 'waiting: another trainer holds the GPU'; 'arm-step train: waiting (another trainer holds the GPU)'; exit 3 }
            Say "training: $(Split-Path $TrainScript -Leaf) $($TrainArgs -join ' ')"
            $trainerArgs = @('-NoProfile', '-File', $TrainScript) + $TrainArgs
            if ($CheckpointEvery -gt 0) { $trainerArgs += @('-CheckpointEvery', "$CheckpointEvery") }
            $r = Start-Process 'pwsh' -ArgumentList $trainerArgs -NoNewWindow -PassThru
            $null = $r.Handle   # keeps the exit code readable once the process has gone
            if (-not $r.WaitForExit([int][math]::Min($Budget.TotalMilliseconds, [int]::MaxValue))) {
                $tree = @(ProcessTree $r.Id)
                Say "the budget of $Budget is up: stopping the trainer's process tree ($($tree -join ', '))"
                foreach ($id in $tree) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
                $r.WaitForExit()
                # A trainer that had already saved its final weights removed its resume file and was on to the next seed or arm
                # (run-r2.ps1 trains several): the step is done, there is nothing to resume.
                if (Test-Path "$TrainDir/$Name.pt") {
                    Say "the budget stopped the trainer after it wrote $Name.pt"
                    "arm-step train: done ($TrainDir/$Name.pt)"
                    exit 0
                }
                $resume = "$TrainDir/$Name.resume.pt"
                if (-not (Test-Path $resume)) { Fail "the budget of $Budget stopped the trainer and it left no $resume to resume from (is -CheckpointEvery 0?)" }
                Say 'resumable: run the train step again'
                'arm-step train: resumable: run the train step again'
                exit 4
            }
            Say "trainer exited $($r.ExitCode)"
            if ($r.ExitCode -ne 0 -or -not (Test-Path "$TrainDir/$Name.pt")) { Fail "the trainer left no $Name.pt (exit $($r.ExitCode))" }
            "arm-step train: done ($TrainDir/$Name.pt)"
        }
        'onnx' {
            $cfg = Get-Content $Config -Raw | ConvertFrom-Json
            $logs = "$($cfg.arm.logs_root)/score-$Name"
            if ((Test-Path "$logs/$Name.onnx") -and (Test-Path "$logs/$Name.contract.json")) { Skipped "$logs/$Name.onnx and its contract exist"; break }
            New-Item -ItemType Directory -Force $Cache, $logs | Out-Null
            if (-not (Test-Path "$Cache/$Name.pt")) {
                if (-not (Test-Path "$TrainDir/$Name.pt")) { Fail "no $TrainDir/$Name.pt to export" }
                Copy-Item "$TrainDir/$Name.pt" "$Cache/$Name.pt"
            }
            Say "exporting $Name to ONNX"
            # The frozen run's own n2n_export.py reads the weights, as score-arm.ps1 does; it writes the contract beside the ONNX.
            $o = Start-Process python -ArgumentList @('n2n_export.py', '--cache', $Cache, '--ckpt', "$Name.pt", '--eval', $Cache, '--out', $logs, '--name', "$Name.onnx") `
                -WorkingDirectory $cfg.arm.run -NoNewWindow -PassThru -Wait -RedirectStandardOutput "$logs/export.log" -RedirectStandardError "$logs/export.err.log"
            if ($o.ExitCode -ne 0 -or -not (Test-Path "$logs/$Name.onnx") -or -not (Test-Path "$logs/$Name.contract.json")) {
                Fail "n2n_export.py exited $($o.ExitCode) and left no $Name.onnx with its contract; see $logs/export.err.log"
            }
            "arm-step onnx: done ($logs/$Name.onnx)"
        }
        'score' {
            $cfg = Get-Content $Config -Raw | ConvertFrom-Json
            $reports = @("real-star-read-$Name.txt", "ring-$Name.txt", "m31-nucleus-$Name.txt") | ForEach-Object { "$($cfg.arm.reports)/$_" }
            if (@($reports | Where-Object { -not (Test-Path $_) }).Count -eq 0) { Skipped "the three reports of $Name exist"; break }
            New-Item -ItemType Directory -Force $Cache | Out-Null
            if (-not (Test-Path "$Cache/$Name.pt")) {
                if (-not (Test-Path "$TrainDir/$Name.pt")) { Fail "no $TrainDir/$Name.pt to score" }
                Copy-Item "$TrainDir/$Name.pt" "$Cache/$Name.pt"
            }
            Say "scoring: score-arm.ps1 -Name $Name"
            $s = Child 'pwsh' @('-NoProfile', '-File', "$PSScriptRoot/score-arm.ps1", '-Name', $Name, '-Weights', "$Cache/$Name.pt", '-Config', $Config)
            if ($s.ExitCode -ne 0) { Fail "score-arm.ps1 exited $($s.ExitCode); see $($cfg.arm.logs_root)/score-$Name/score.log" }
            "arm-step score: done (the reports of $Name in $($cfg.arm.reports))"
        }
        'whole' {
            $cfg = Get-Content $Config -Raw | ConvertFrom-Json
            $row = "$($cfg.out_root)/$WholeTag/row.txt"
            if (Test-Path $row) { Skipped "$row exists"; break }
            $onnx = "$($cfg.arm.logs_root)/score-$Name/$Name.onnx"
            if (-not (Test-Path $onnx)) { Fail "no $onnx for the whole-master scorecard" }
            Say "the whole-master scorecard (score_whole.py $WholeTag)"
            $w = Child 'python' @('-I', "$PSScriptRoot/score_whole.py", $WholeTag, '--config', $Config, '--model', $onnx)
            if ($w.ExitCode -ne 0 -or -not (Test-Path $row)) { Fail "score_whole.py exited $($w.ExitCode); see $($cfg.out_root)/$WholeTag/score.log" }
            "arm-step whole: done ($row)"
        }
    }
}
catch {
    # Once: a Fail the step raised and an exception it did not expect are one failure, one line in the log, one stop file.
    if (-not (Test-Path $stop)) { Halt "$_" }
    throw
}
