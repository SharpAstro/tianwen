<#
.SYNOPSIS
    Builds one of TianWen's desktop apps and starts it DETACHED, printing its pid and where its stdout and
    stderr go. The launcher behind the run-gui, run-fits and run-tui skills.

.DESCRIPTION
    An app started as a Claude Code background shell lives and dies with that shell, and Claude Code reaps
    background shells when the machine runs low on memory while a session is idle: the GUI was lost that way
    on 2026-09-19. This script builds in the FOREGROUND, so a build error comes back to the caller instead of
    into a log nobody reads, then starts the built executable with Start-Process and returns. The app belongs
    to no shell, so nothing but the user closes it.

    It never stops a running instance, since closing one costs the user whatever they were doing in it. With
    one already running it refuses (the build would fail on the locked binaries anyway) unless
    -AllowSecondInstance, which gives the second instance its own logs.

    A detached app hands back no exit code. Its stderr log is the evidence of a crash (.NET writes an unhandled
    exception there), and Get-Process -Id <pid> says whether it still runs. A launch that dies inside its first
    three seconds is reported here, with its exit code and its stderr.

    Environment variables the caller sets (TIANWEN_NOW, say) reach the app, which inherits this process's.

.PARAMETER App
    gui (tianwen-gui), fits (tianwen-fits) or tui (the CLI's TUI, in a console window of its own).

.PARAMETER AppArgs
    Passed to the app: a file or folder for fits, anything after "tui" for the TUI. Put -- before them when one
    starts with a dash, or PowerShell reads it as a parameter of this script.

.PARAMETER Configuration
    Debug by default, because the live UI inspector exists only in a Debug build.

.PARAMETER Validation
    Load the Khronos Vulkan validation layer (SDLVK_VALIDATION=1). Debug only, and several times slower, so
    only when chasing a GPU bug.

.PARAMETER SyncValidation
    Add synchronisation-hazard validation (SDLVK_SYNC_VALIDATION=1). Implies -Validation; slower still.

.PARAMETER NoBuild
    Start the last build as it is.

.PARAMETER AllowSecondInstance
    Start although one is running. Its logs carry a timestamp, so the two never share a file.

.PARAMETER NoColor
    Start the app with NO_COLOR=1, to see the TUI as a colourless terminal shows it (the selection is reverse
    video there). Without it the launcher CLEARS NO_COLOR, which the calling shell may carry.

.EXAMPLE
    pwsh -NoProfile -File tools/start-app.ps1 gui

.EXAMPLE
    pwsh -NoProfile -File tools/start-app.ps1 fits D:\Astro\M42\master.fits

.EXAMPLE
    pwsh -NoProfile -File tools/start-app.ps1 gui -Validation -SyncValidation
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)] [ValidateSet('gui', 'fits', 'tui')] [string] $App,
    [Parameter(Position = 1, ValueFromRemainingArguments)] [string[]] $AppArgs,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $Validation,
    [switch] $SyncValidation,
    [switch] $NoBuild,
    [switch] $AllowSecondInstance,
    [switch] $NoColor
)

$ErrorActionPreference = 'Stop'

$apps = @{
    gui  = @{ Project = 'TianWen.UI.Gui'; Exe = 'tianwen-gui'; Log = 'gui' }
    fits = @{ Project = 'TianWen.UI.FitsViewer'; Exe = 'tianwen-fits'; Log = 'fitsviewer' }
    tui  = @{ Project = 'TianWen.Cli'; Exe = 'tianwen'; Log = 'tui' }
}
$spec = $apps[$App]
$src = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$AppArgs = @($AppArgs | Where-Object { $_ })

if ($App -eq 'tui' -and -not $IsWindows) {
    throw 'The TUI launch opens a Windows console window; on another OS run it in a terminal of its own.'
}

# The TUI shares its executable with every other CLI command (a detached dataset bake is tianwen.exe too), so
# only a process whose command line says "tui" counts as one.
function Get-RunningInstance {
    if ($IsWindows) {
        $found = @(Get-CimInstance Win32_Process -Filter "Name = '$($spec.Exe).exe'")
        if ($App -eq 'tui') {
            $found = @($found | Where-Object { $_.CommandLine -match '(^|\s)tui(\s|$)' })
        }
        return @($found | ForEach-Object { $_.ProcessId })
    }
    return @(Get-Process -Name $spec.Exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
}

$running = Get-RunningInstance
if ($running.Count -gt 0 -and -not $AllowSecondInstance) {
    Write-Host "$($spec.Exe) is already running (pid $($running -join ', ')); not starting another, and not closing it."
    Write-Host 'Ask the user to close it, or pass -AllowSecondInstance.'
    exit 1
}

if (-not $NoBuild) {
    $project = Join-Path $src "$($spec.Project)/$($spec.Project).csproj"
    Write-Host "building $($spec.Project) ($Configuration)..."
    $build = @(& dotnet build $project -c $Configuration -nologo 2>&1 | ForEach-Object { "$_" })
    $warnings = @($build | Where-Object { $_ -match ': warning ' } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) {
        $build | Where-Object { $_ -match ': error ' } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        $locked = @(Get-Process -Name $spec.Exe -ErrorAction SilentlyContinue)
        if ($locked.Count -gt 0) {
            Write-Host "note: $($spec.Exe) is running (pid $(@($locked.Id) -join ', ')) and may hold the binaries the build writes."
        }
        Write-Host "build FAILED (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
    $warnings | ForEach-Object { Write-Host $_ }
    Write-Host "built, $($warnings.Count) warning(s)"
}

$extension = if ($IsWindows) { '.exe' } else { '' }
$exe = Join-Path $src "$($spec.Project)/bin/$Configuration/net10.0/$($spec.Exe)$extension"
if (-not (Test-Path $exe)) {
    throw "no build at $exe; run without -NoBuild"
}

$suffix = if ($running.Count -gt 0) { '-' + (Get-Date -Format 'HHmmss') } else { '' }
$stdout = Join-Path $src "$($spec.Log)$suffix-stdout.log"
$stderr = Join-Path $src "$($spec.Log)$suffix-stderr.log"

# Start-Process joins its argument list with spaces and quotes nothing, so a path with a space would split.
function Format-Argument([string] $value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}
$quotedArgs = ($AppArgs | ForEach-Object { Format-Argument $_ }) -join ' '

# Set for the app to inherit, and put back afterwards in case this script was run inside a session.
$savedValidation = $env:SDLVK_VALIDATION
$savedSyncValidation = $env:SDLVK_SYNC_VALIDATION

# The app is for a person at a real terminal, not for the tool that launched it: an agent's shell sets NO_COLOR
# to keep its own output plain, and the TUI inherited it and drew with no colour at all -- no selection bar, no
# pinned tint, no tab highlight, so a click looked as if it selected nothing (2026-09-19).
$savedNoColor = $env:NO_COLOR
$env:NO_COLOR = if ($NoColor) { '1' } else { $null }
if ($SyncValidation) { $Validation = [switch]$true; $env:SDLVK_SYNC_VALIDATION = '1' }
if ($Validation) {
    $env:SDLVK_VALIDATION = '1'
    if ($Configuration -ne 'Debug') { Write-Warning 'validation loads only in a Debug build' }
}

try {
    if ($App -eq 'tui') {
        # The TUI draws on stdout, so it needs a console of its own and only stderr can go to a file. cmd /k
        # keeps the window open after the TUI exits, so a final stack trace stays readable, and prints the exit
        # code: delayed expansion (/v:on, !ERRORLEVEL!), because %ERRORLEVEL% on one line expands before the
        # TUI has run.
        $line = "/v:on /k title TianWen TUI& `"$exe`" tui $quotedArgs 2> `"$stderr`"& echo TUI exited with code !ERRORLEVEL!"
        $console = Start-Process -FilePath 'cmd.exe' -ArgumentList $line -WorkingDirectory (Get-Location).Path -PassThru
        $appPid = $null
        for ($i = 0; $i -lt 50 -and -not $appPid; $i++) {
            $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($console.Id) AND Name = '$($spec.Exe).exe'"
            if ($child) { $appPid = @($child)[0].ProcessId } else { Start-Sleep -Milliseconds 100 }
        }
        if (-not $appPid) {
            Write-Host "the console window started (pid $($console.Id)) but no $($spec.Exe) appeared in it within five seconds"
            Write-Host "stderr: $stderr"
            exit 1
        }
        Write-Host "$($spec.Exe) tui started detached ($Configuration), in its own console window"
        Write-Host "pid:     $appPid (console window: cmd pid $($console.Id))"
        Write-Host 'stdout:  the console window'
        Write-Host "stderr:  $stderr"
        exit 0
    }

    $startArgs = @{
        FilePath               = $exe
        WorkingDirectory       = (Get-Location).Path
        RedirectStandardOutput = $stdout
        RedirectStandardError  = $stderr
        PassThru               = $true
    }
    if ($quotedArgs) { $startArgs.ArgumentList = $quotedArgs }
    $process = Start-Process @startArgs
}
finally {
    $env:SDLVK_VALIDATION = $savedValidation
    $env:SDLVK_SYNC_VALIDATION = $savedSyncValidation
    $env:NO_COLOR = $savedNoColor
}

# Catch a launch that cannot even start (a missing native library, a bad argument): it is the one crash a caller
# would otherwise only find by reading a log it did not know to read.
if ($process.WaitForExit(3000)) {
    Write-Host "$($spec.Exe) EXITED within three seconds, code $($process.ExitCode); its stderr:"
    Get-Content $stderr -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "$($spec.Exe) started detached ($Configuration)$(if ($Validation) { ', Vulkan validation on' })"
Write-Host "pid:     $($process.Id)"
Write-Host "stdout:  $stdout"
Write-Host "stderr:  $stderr"
