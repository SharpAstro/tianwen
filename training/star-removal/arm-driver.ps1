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
# files are in the runs folder (TIANWEN_RUNS, default C:/temp/e2, as training/azure/common.ps1). A step that fails writes the stop
# file, so a failing step is not retried every 10 minutes for a night; the log says which and why.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    # The folder the trained weights land in, and the scoring cache they are copied to (score-arm.ps1 reads <Cache>/<Name>.pt).
    [Parameter(Mandatory)] [string]$TrainDir,
    [Parameter(Mandatory)] [string]$Cache,
    [string]$Config = (Join-Path $PSScriptRoot 'scorecard.local.json'),
    [string]$PreReg = '',
    [string]$ExportScript = '',
    [string]$ExportDone = '',
    [string]$TrainScript = '',
    [string[]]$TrainArgs = @(),
    # The tag score_whole.py writes the row under, when it is not -Tag.
    [string]$WholeTag = ''
)
$ErrorActionPreference = 'Stop'
if (-not $WholeTag) { $WholeTag = $Tag }
$runs = if ($env:TIANWEN_RUNS) { $env:TIANWEN_RUNS } else { 'C:/temp/e2' }
$status = "$runs/$Tag-chain.status"
$log = "$runs/$Tag-chain.log"
$stop = "$runs/$Tag-chain.stop"
$done = "$runs/$Tag.done"
function Say([string]$what) { "$(Get-Date -Format o) $what" | Out-File $log -Append -Encoding utf8; "$what $(Get-Date -Format o)" | Out-File $status -Encoding utf8 }
function Running([string]$pattern) { @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match $pattern }).Count -gt 0 }
function Halt([string]$why) { Say "FAILED: $why"; New-Item -ItemType File $stop -Force | Out-Null }

if (Test-Path $done) { return }
if (Test-Path $stop) { return }
$n = [regex]::Escape($Name)
if ($ExportScript -and (Running ([regex]::Escape((Split-Path $ExportScript -Leaf))))) { return }
if ($TrainScript -and (Running ([regex]::Escape((Split-Path $TrainScript -Leaf))))) { return }
if (Running "n2n_smoke\.py.*$n") { return }
if (Running "score-arm\.ps1.*$n|r2_infer\.py.*$n|starless-eval.*$n|n2n_export\.py.*$n|real_star_check\.py|ring_annulus\.py|score_whole\.py|remove-stars") { return }

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
        if (Running 'n2n_smoke\.py --train') { Say 'waiting: another trainer holds the GPU'; return }
        Say "training: $(Split-Path $TrainScript -Leaf) $($TrainArgs -join ' ')"
        $r = Start-Process pwsh -ArgumentList (@('-NoProfile', '-File', $TrainScript) + $TrainArgs) -NoNewWindow -PassThru -Wait
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
