# Registers arm-driver.ps1 as the scheduled task tw-<tag>: every 10 minutes, IgnoreNew (a pass still running skips the next),
# priority 4 (normal; Task Scheduler's default 7 starved a task's Python on a busy machine, 2026-10-15), and the action
# through `conhost.exe --headless` so Windows Terminal opens no empty window. Everything after -Tag is the driver's own
# arguments:
#   pwsh -File register-arm-task.ps1 -Tag r2v -DriverArgs '-Name','r2v_synth10_s0','-TrainDir','D:/x','-Cache','C:/y','-Runs','C:/z'
# (-Runs: the driver's runs folder, which a task's environment may not know through TIANWEN_RUNS.)
# Check it with the real program before relying on it: start the task and watch the driver's log move.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string[]]$DriverArgs
)
$ErrorActionPreference = 'Stop'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$driver = Join-Path $PSScriptRoot 'arm-driver.ps1'
$quoted = ($DriverArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless `"$pwsh`" -NoProfile -File `"$driver`" -Tag $Tag $quoted"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 10)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Days 3) -StartWhenAvailable
$settings.Priority = 4
Register-ScheduledTask -TaskName "tw-$Tag" -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
"registered tw-$Tag (every 10 minutes, priority 4, conhost --headless)"
