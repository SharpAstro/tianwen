# The driver of one star-remover arm trained on THIS machine (the GTX 1070), as a RESUMABLE pass that Task Scheduler re-runs every
# 10 minutes (register-arm-task.ps1: task tw-<tag>, conhost --headless, priority 4, every child -NoNewWindow, IgnoreNew). The
# Azure arms have training/azure/az-driver.ps1, which ends in score-arm.ps1 as its -AfterScript; this is the same shape for an
# arm that trains here. A pass does the first step whose output is missing and ends, so a pass is never long and a crash costs
# at most one step:
#   1. exits at once when the arm is done (<tag>.done), stopped (<tag>-chain.stop), or one of its workers still runs;
#   2. waits for the pre-registration file, when -PreReg names one (a run is registered before it is looked at);
#   3. exports the training set (-ExportScript, itself resumable, done when -ExportDone exists);
#   4. trains (-TrainScript, which leaves <TrainDir>/<Name>.pt);
#   5. copies the weights into -Cache and runs score-arm.ps1 (skips every step whose output exists), then score_whole.py over
#      the ONNX that scoring wrote, unless <out root>/<WholeTag>/row.txt is already there;
#   6. writes <tag>.done.
# Stop it with <tag>-chain.stop in the runs folder, then `schtasks /Change /TN tw-<tag> /DISABLE`. Status, log, stop and done
# files are in the runs folder: -Runs, or TIANWEN_RUNS (the variable training/azure/common.ps1 reads); one of them is required,
# since this file names no machine path. A step that fails writes the stop
# file, so a failing step is not retried every 10 minutes for a night; the log says which and why.
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
if (Running $py "(r2_infer|n2n_export)\.py.*$n|(real_star_check|ring_annulus|score_whole)\.py") { return }
if (Running @('tianwen.exe') "starless-eval.*$n|\bremove-stars\b") { return }

try {
    $cfg = Get-Content $Config -Raw | ConvertFrom-Json
    if ($PreReg -and -not (Test-Path $PreReg)) { Say "waiting for the pre-registration ($PreReg)"; return }
    if ($ExportScript -and $ExportDone -and -not (Test-Path $ExportDone)) {
        Say "exporting ($(Split-Path $ExportScript -Leaf), resumable)"
        $x = Start-Process pwsh -ArgumentList (@('-NoProfile', '-File', $ExportScript)) -NoNewWindow -PassThru -Wait
        if (-not (Test-Path $ExportDone)) { Halt "the export ended (exit $($x.ExitCode)) without $ExportDone"; return }
        Say 'exported'
    }
    if (-not (Test-Path "$TrainDir/$Name.pt")) {
        if (-not $TrainScript) { Halt "no $TrainDir/$Name.pt and no -TrainScript"; return }
        if (Running $py $GpuTrainerPattern) { Say 'waiting: another trainer holds the GPU'; return }
        $trainArgs = @($TrainArgs)
        if ($SpeckleWeight -gt 0) {
            $trainArgs += '-SpeckleWeight', $SpeckleWeight.ToString([cultureinfo]::InvariantCulture)
            $trainArgs += '-SpeckleSigma', $SpeckleSigma.ToString([cultureinfo]::InvariantCulture)
        }
        Say "training: $(Split-Path $TrainScript -Leaf) $($trainArgs -join ' ')"
        $r = Start-Process pwsh -ArgumentList (@('-NoProfile', '-File', $TrainScript) + $trainArgs) -NoNewWindow -PassThru -Wait
        Say "trainer exited $($r.ExitCode)"
        if ($r.ExitCode -ne 0 -or -not (Test-Path "$TrainDir/$Name.pt")) { Halt "the trainer left no $Name.pt (exit $($r.ExitCode))"; return }
    }
    New-Item -ItemType Directory -Force $Cache | Out-Null
    if (-not (Test-Path "$Cache/$Name.pt")) { Copy-Item "$TrainDir/$Name.pt" "$Cache/$Name.pt" }
    Say "scoring: score-arm.ps1 -Name $Name"
    $s = Start-Process pwsh -ArgumentList '-NoProfile', '-File', "$PSScriptRoot/score-arm.ps1", '-Name', $Name, '-Weights', "$Cache/$Name.pt", '-Config', $Config `
        -NoNewWindow -PassThru -Wait
    if ($s.ExitCode -ne 0) { Halt "score-arm.ps1 exited $($s.ExitCode); see $($cfg.arm.logs_root)/score-$Name/score.log"; return }
    $row = "$($cfg.out_root)/$WholeTag/row.txt"
    if (-not (Test-Path $row)) {
        $onnx = "$($cfg.arm.logs_root)/score-$Name/$Name.onnx"
        if (-not (Test-Path $onnx)) { Halt "no $onnx for the whole-master scorecard"; return }
        Say "the whole-master scorecard (score_whole.py $WholeTag)"
        $w = Start-Process python -ArgumentList '-I', "$PSScriptRoot/score_whole.py", $WholeTag, '--config', $Config, '--model', $onnx `
            -NoNewWindow -PassThru -Wait
        if ($w.ExitCode -ne 0 -or -not (Test-Path $row)) { Halt "score_whole.py exited $($w.ExitCode); see $($cfg.out_root)/$WholeTag/score.log"; return }
    }
    "scored $(Get-Date -Format o)" | Out-File $done -Encoding utf8
    Say "done: scored; $Name's eval folders, the real-star, ring and nucleus reads and $row written"
}
catch {
    Say "FAILED: $_"
    throw
}
