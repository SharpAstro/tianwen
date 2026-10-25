# The driver of one star-remover arm trained on THIS machine (the GTX 1070), as a RESUMABLE pass that Task Scheduler re-runs every
# 10 minutes (register-arm-task.ps1: task tw-<tag>, conhost --headless, priority 4, every child -NoNewWindow, IgnoreNew). The
# Azure arms have training/azure/az-driver.ps1, which ends in score-arm.ps1 as its -AfterScript; this is the same shape for an
# arm that trains here. The work itself is arm-step.ps1's, one step per call (export, train, score, whole), each of which a pilot
# job can run alone; this file is a pass over those steps, so the task and a job run the same code. A pass does the first step
# whose output is missing and ends, so a pass is never long and a crash costs at most one step:
#   1. exits at once when the arm is done (<tag>.done), stopped (<tag>-chain.stop), or one of its workers still runs (a step run
#      alone as a job included);
#   2. waits for the pre-registration file, when -PreReg names one (a run is registered before it is looked at);
#   3. runs the steps in order, each skipping itself when its output exists: export (-ExportScript, itself resumable, done when
#      -ExportDone exists), train (-TrainScript, which leaves <TrainDir>/<Name>.pt), score (the weights into -Cache, then
#      score-arm.ps1), whole (score_whole.py over the ONNX that scoring wrote, unless <out root>/<WholeTag>/row.txt is there);
#   4. writes <tag>.done once every step has skipped.
# Stop it with <tag>-chain.stop in the runs folder, then `schtasks /Change /TN tw-<tag> /DISABLE`. Status, log, stop and done
# files are in the runs folder: -Runs, or TIANWEN_RUNS (the variable training/azure/common.ps1 reads); one of them is required,
# since this file names no machine path. A step that fails, and any other exception, writes the stop file (once), so a failure
# is not retried every 10 minutes for a night; the log says which and why.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    # The folder the trained weights land in, and the scoring cache they are copied to (score-arm.ps1 reads <Cache>/<Name>.pt).
    [Parameter(Mandatory)] [string]$TrainDir,
    [Parameter(Mandatory)] [string]$Cache,
    [string]$Config = (Join-Path $PSScriptRoot 'scorecard.local.json'),
    # The runs folder; TIANWEN_RUNS when not given. A scheduled task passes it, since its environment may not carry the variable.
    [string]$Runs = $env:TIANWEN_RUNS,
    # A python command line that means another trainer holds the GPU, which the driver then waits for.
    [string]$GpuTrainerPattern = 'n2n_smoke\.py --train',
    [string]$PreReg = '',
    [string]$ExportScript = '',
    [string]$ExportDone = '',
    [string]$TrainScript = '',
    [string[]]$TrainArgs = @(),
    # #1362: the speckle teacher, handed to the train script as -SpeckleWeight / -SpeckleSigma (training/denoise/run-r2.ps1 takes
    # them, as n2n_smoke.py's --speckle-weight / --speckle-sigma); 0 (default) passes nothing, so a train script that has no such
    # parameter is untouched.
    [double]$SpeckleWeight = 0,
    [double]$SpeckleSigma = 100,
    # The tag score_whole.py writes the row under, when it is not -Tag.
    [string]$WholeTag = ''
)
$ErrorActionPreference = 'Stop'
if (-not $WholeTag) { $WholeTag = $Tag }
if (-not $Runs) { throw 'no runs folder: pass -Runs or set TIANWEN_RUNS' }
$runs = $Runs
$status = "$runs/$Tag-chain.status"
$log = "$runs/$Tag-chain.log"
$stop = "$runs/$Tag-chain.stop"
$done = "$runs/$Tag.done"
function Say([string]$what) { "$(Get-Date -Format o) $what" | Out-File $log -Append -Encoding utf8; "$what $(Get-Date -Format o)" | Out-File $status -Encoding utf8 }
# A worker is a process of one of $names (never a shell or an editor session that merely MENTIONS a script in its prompt) whose
# command line matches $pattern. The driver's own process tree is excluded: its command line carries -TrainScript and
# -ExportScript as arguments, and so does its conhost parent under the scheduled task.
$self = @{}
for ($id = $PID; $id -and -not $self.ContainsKey([int]$id); ) {
    $self[[int]$id] = $true
    $id = (Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue).ParentProcessId
}
function Running([string[]]$names, [string]$pattern) {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.CommandLine -and -not $self.ContainsKey([int]$_.ProcessId) -and $_.CommandLine -notmatch 'arm-driver\.ps1' `
            -and ($names -contains $_.Name) -and $_.CommandLine -match $pattern
    }).Count -gt 0
}
# A pwsh running a script: its own -File argument names it, which the driver's -TrainScript argument never does.
function RunningScript([string]$script) { Running @('pwsh.exe', 'powershell.exe') ('-File\s+"?[^\s"]*' + [regex]::Escape((Split-Path $script -Leaf)) + '(\s|"|$)') }
function Halt([string]$why) { Say "FAILED: $why"; New-Item -ItemType File $stop -Force | Out-Null }

if (Test-Path $done) { return }
if (Test-Path $stop) { return }
$n = [regex]::Escape($Name)
if ($ExportScript -and (RunningScript $ExportScript)) { return }
if ($TrainScript -and (RunningScript $TrainScript)) { return }
$py = @('python.exe', 'python3.exe', 'py.exe')
if (Running $py "n2n_smoke\.py.*$n") { return }
if (Running @('pwsh.exe') "-File\s+\S*score-arm\.ps1.*$n") { return }
# A step run alone (a pilot job: arm-step.ps1 -Step ... -Tag <tag>); the driver's own steps are children it has not started yet.
if (Running @('pwsh.exe') ('-File\s+"?\S*arm-step\.ps1.*-Tag\s+' + [regex]::Escape($Tag) + '(\s|$)')) { return }
if (Running $py "(r2_infer|n2n_export)\.py.*$n|(real_star_check|ring_annulus|score_whole)\.py") { return }
if (Running @('tianwen.exe') "starless-eval.*$n|\bremove-stars\b") { return }

try {
    $cfg = Get-Content $Config -Raw | ConvertFrom-Json
    if ($PreReg -and -not (Test-Path $PreReg)) { Say "waiting for the pre-registration ($PreReg)"; return }
    # The steps in arm-step.ps1's order. A step whose output exists says "skipped" and the pass goes on to the next; the first one
    # that does work ends the pass, so a pass is never long and a crash costs at most one step. A failing step has written the
    # stop file itself (arm-step.ps1 does, on any exception), which the next pass reads.
    foreach ($step in 'export', 'train', 'score', 'whole') {
        $out = Join-Path ([IO.Path]::GetTempPath()) "$Tag-$step-$PID.out"
        $stepArgs = @('-NoProfile', '-File', "$PSScriptRoot/arm-step.ps1", '-Step', $step, '-Tag', $Tag, '-Name', $Name, '-TrainDir', $TrainDir,
            '-Cache', $Cache, '-Config', $Config, '-Runs', $runs, '-GpuTrainerPattern', $GpuTrainerPattern, '-WholeTag', $WholeTag)
        # An empty value would vanish from the command line and leave its switch eating the next one.
        foreach ($optional in @(@('-ExportScript', $ExportScript), @('-ExportDone', $ExportDone), @('-TrainScript', $TrainScript))) {
            if ($optional[1]) { $stepArgs += $optional }
        }
        # #1362: the speckle teacher goes to the train script as -SpeckleWeight / -SpeckleSigma; 0 passes nothing.
        $trainArgs = @($TrainArgs)
        if ($SpeckleWeight -gt 0) {
            $trainArgs += '-SpeckleWeight', $SpeckleWeight.ToString([cultureinfo]::InvariantCulture)
            $trainArgs += '-SpeckleSigma', $SpeckleSigma.ToString([cultureinfo]::InvariantCulture)
        }
        # Under -File an array is one comma-joined string, which arm-step.ps1 splits again.
        if ($trainArgs.Count) { $stepArgs += @('-TrainArgs', ($trainArgs -join ',')) }
        # Start-Process joins its arguments without quoting any (the default GPU pattern has a space in it).
        $stepArgs = @($stepArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
        $p = Start-Process pwsh -ArgumentList $stepArgs -NoNewWindow -PassThru -Wait -RedirectStandardOutput $out -RedirectStandardError "$out.err"
        $said = (Get-Content $out -ErrorAction SilentlyContinue) -join ' '
        $complained = (Get-Content "$out.err" -ErrorAction SilentlyContinue) -join ' '
        Remove-Item $out, "$out.err" -ErrorAction SilentlyContinue
        # 3 is "another trainer holds the GPU": nothing was done and nothing failed.
        if ($p.ExitCode -eq 3) { return }
        if ($p.ExitCode -ne 0) { if (-not (Test-Path $stop)) { Halt "arm-step.ps1 -Step $step exited $($p.ExitCode): $complained" }; return }
        if ($said -notmatch 'skipped') { return }
    }
    "scored $(Get-Date -Format o)" | Out-File $done -Encoding utf8
    Say "done: scored; $Name's eval folders, the real-star, ring and nucleus reads and $($cfg.out_root)/$WholeTag/row.txt written"
}
catch {
    # Halt as a failing step does (#1787): without the stop file a config that cannot load, or any exception no step named, was
    # tried again every 10 minutes for a night. Once: a step's own failure has written it already.
    if (-not (Test-Path $stop)) { Halt "$_" }
    throw
}
