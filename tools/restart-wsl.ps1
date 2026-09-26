<#
.SYNOPSIS
    Restarts a wedged WSL service, clears the probe processes it stranded, and checks WSL answers again.

.DESCRIPTION
    A wedged WSL service answers nothing, `wsl --shutdown` and `wsl --list` included, and every `wsl ...`
    started against it hangs for good. Before the plate solvers bounded their probe (2026-09-27), each
    process that looked for astrometry.net left a hung `wsl solve-field -h` pair behind, 2,444 of them in
    one night. Restarting the service needs an administrator, so this script relaunches itself elevated
    when it is not.

    Steps: stop WslService (and force-kill wslservice.exe if it will not stop within the timeout), start it,
    kill any leftover `wsl solve-field -h` / `wsl --shutdown` / `wsl --list` clients, then ask
    `wsl --list --verbose` with a timeout. Docker Desktop runs on WSL, so restart it afterwards.

.EXAMPLE
    pwsh -NoProfile -File tools/restart-wsl.ps1
#>
param([int]$TimeoutSeconds = 30)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Not elevated; relaunching as administrator (accept the UAC prompt).'
    Start-Process pwsh -Verb RunAs -Wait -ArgumentList '-NoProfile', '-File', "`"$PSCommandPath`"", '-TimeoutSeconds', $TimeoutSeconds
    return
}

function Wait-Service([string]$name, [string]$state, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Service $name).Status -ne $state -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    return (Get-Service $name).Status -eq $state
}

try {
    Write-Host 'Stopping WslService...'
    Stop-Service WslService -Force -NoWait -ErrorAction SilentlyContinue
    if (-not (Wait-Service WslService Stopped $TimeoutSeconds)) {
        Write-Host "  did not stop within $TimeoutSeconds s; killing wslservice.exe"
        Get-Process wslservice -ErrorAction SilentlyContinue | Stop-Process -Force
        [void](Wait-Service WslService Stopped 10)
    }

    $stranded = Get-CimInstance Win32_Process -Filter "Name='wsl.exe'" |
        Where-Object { $_.CommandLine -match 'solve-field -h|--shutdown|--list' }
    if ($stranded) {
        Write-Host "Killing $(@($stranded).Count) stranded wsl.exe clients"
        $stranded | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }

    Write-Host 'Starting WslService...'
    Start-Service WslService
    [void](Wait-Service WslService Running $TimeoutSeconds)
    Write-Host "  WslService: $((Get-Service WslService).Status)"

    $job = Start-Job { wsl.exe --list --verbose 2>&1 | Out-String }
    if (Wait-Job $job -Timeout $TimeoutSeconds) {
        Write-Host 'wsl --list --verbose:'
        Write-Host (((Receive-Job $job) -replace "`0", '').Trim())
        Write-Host 'WSL answers. Restart Docker Desktop if you use it.'
    }
    else {
        Write-Host "wsl --list still did not answer within $TimeoutSeconds s; a reboot is the remaining fix."
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}
finally {
    Read-Host 'Press Enter to close'
}
