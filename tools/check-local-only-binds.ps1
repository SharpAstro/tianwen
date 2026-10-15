<#
.SYNOPSIS
    Proves the local-only mode (TIANWEN_LOCAL_ONLY): starts tianwen-server and the Debug GUI in the mode on a scratch
    data root, runs a full device discovery through the node's API, and lists every TCP listener and UDP endpoint the
    GUI, the node and any keeper own. Exits 1 if any local address is not loopback.

.DESCRIPTION
    Windows Defender Firewall asks once per executable PATH when a process binds a non-loopback address, never for
    loopback, so a build in a new folder (a worktree) raises a prompt on the desk unless it is local-only. This is the
    end-to-end check of that promise; the unit tests (LocalOnlyModeTests, LocalOnlyModeSourceTests) are its negative
    control, since running a build WITHOUT the mode would raise exactly the prompt the mode exists to avoid.

    What it does, in order:
    1. Builds TianWen.UI.Gui (tianwen-server is built into the same folder), unless -NoBuild.
    2. Copies that output to a scratch folder, leaving out -OmitNative: by default toupcam.dll, because the ToupTek
       source OPENS every camera it enumerates to read its serial, and a check must not open a camera another program
       may be streaming from. Every other source runs its full discovery (ASCOM, Alpaca, Canon, the serial probes,
       the network discovery the mode must skip).
    3. Sets TIANWEN_DATA_ROOT to a fresh folder in the scratch, so it never touches the user's node, profiles or socket,
       and TIANWEN_LOCAL_ONLY=1 (the environment, not --local-only, is what it proves), then starts tianwen-server.
    4. POST /api/v1/devices/discover over the node's socket and waits for the job.
    5. Starts the Debug GUI (TIANWEN_GUI_SINGLE_INSTANCE=0, so a GUI already open elsewhere is left alone), waits until
       it is attached to the node (or has started its own), discovers once more on whichever node is there, and lets
       both settle.
    Throughout, it samples the endpoints of every process it is watching, since a discovery socket lives seconds, then
    prints them all. Every process it started is stopped by pid at the end (the node through its own shutdown first),
    and the scratch folder is removed unless -KeepScratch.

.PARAMETER Scratch
    The folder to work in; a new one under the temp folder by default. Must not exist yet.

.PARAMETER SettleSeconds
    How long to keep sampling once the GUI is attached and the second discovery has ended.

.PARAMETER OmitNative
    Native libraries left out of the copy, by file name.

.PARAMETER NoBuild
    Use the last Debug build of TianWen.UI.Gui as it is.

.PARAMETER Placement
    Where the GUI's window goes as soon as it exists: Left (the default) moves it onto the left half of the primary
    monitor's work area, full height, so it never covers the right half, where another program may need to be seen;
    AsIs leaves it where it opens.

.PARAMETER KeepScratch
    Leave the scratch folder (the copy, the data root and every log) for inspection.

.EXAMPLE
    pwsh -NoProfile -File tools/check-local-only-binds.ps1
#>
[CmdletBinding()]
param(
    [string] $Scratch = (Join-Path ([IO.Path]::GetTempPath()) ("tianwen-local-only-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int] $SettleSeconds = 15,
    [string[]] $OmitNative = @('toupcam.dll'),
    [ValidateSet('Left', 'AsIs')] [string] $Placement = 'Left',
    [switch] $NoBuild,
    [switch] $KeepScratch
)

$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'This check reads Windows'' own endpoint tables (Get-NetTCPConnection, Get-NetUDPEndpoint) and runs on Windows only.'
}

# As in start-app.ps1: make this script's stdout and stderr non-inheritable before it starts anything, or the app and
# the build's worker nodes inherit the caller's output pipe and the caller waits for them to exit.
Add-Type -Namespace TianWenLocalOnlyCheck -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern System.IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetHandleInformation(System.IntPtr hObject, uint dwMask, uint dwFlags);
'@
foreach ($std in -11, -12) {
    [void][TianWenLocalOnlyCheck.Native]::SetHandleInformation([TianWenLocalOnlyCheck.Native]::GetStdHandle($std), 1, 0)
}

# Placing the GUI's window (-Placement Left): the primary monitor's work area and the window, in physical pixels, since the
# thread asks per-monitor DPI awareness for the calls, as the SDL window itself is.
Add-Type -Namespace TianWenLocalOnlyCheck -Name Window -MemberDefinition @'
public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool IsZoomed(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern System.IntPtr SetThreadDpiAwarenessContext(System.IntPtr dpiContext);
'@

# Moves the process's main window onto the left half of the primary monitor's work area, full height, as soon as it has one,
# without activating it; un-maximises it first. Reports where it ended up.
function Move-ToLeftHalf([System.Diagnostics.Process] $Process, [int] $Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $Process.HasExited) {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 50
    }
    $window = $Process.MainWindowHandle
    if ($Process.HasExited -or $window -eq [IntPtr]::Zero) {
        Write-Host "the GUI showed no window within $Seconds s to place"
        return
    }

    $previous = [TianWenLocalOnlyCheck.Window]::SetThreadDpiAwarenessContext([IntPtr](-4))   # per-monitor aware, v2
    try {
        $area = [TianWenLocalOnlyCheck.Window+RECT]::new()
        [void][TianWenLocalOnlyCheck.Window]::SystemParametersInfo(0x30, 0, [ref]$area, 0)    # SPI_GETWORKAREA
        if ([TianWenLocalOnlyCheck.Window]::IsZoomed($window)) {
            [void][TianWenLocalOnlyCheck.Window]::ShowWindow($window, 4)                       # SW_SHOWNOACTIVATE
        }
        $half = [int][Math]::Floor(($area.Right - $area.Left) / 2)
        [void][TianWenLocalOnlyCheck.Window]::SetWindowPos($window, [IntPtr]::Zero, $area.Left, $area.Top, $half, $area.Bottom - $area.Top, 0x14)   # NOZORDER, NOACTIVATE
        $placed = [TianWenLocalOnlyCheck.Window+RECT]::new()
        [void][TianWenLocalOnlyCheck.Window]::GetWindowRect($window, [ref]$placed)
        $onTheLeft = $placed.Right -le $area.Left + $half + 16   # the invisible resize border may reach a few pixels past
        Write-Host ("GUI window {0} the left half: ({1},{2})-({3},{4}) of the work area ({5},{6})-({7},{8})" -f
            $(if ($onTheLeft) { 'placed on' } else { 'NOT on' }), $placed.Left, $placed.Top, $placed.Right, $placed.Bottom,
            $area.Left, $area.Top, $area.Right, $area.Bottom)
    }
    finally {
        [void][TianWenLocalOnlyCheck.Window]::SetThreadDpiAwarenessContext($previous)
    }
}

$src = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$guiOut = Join-Path $src 'TianWen.UI.Gui/bin/Debug/net10.0'
$curl = Join-Path $env:SystemRoot 'System32/curl.exe'

$Scratch = [IO.Path]::GetFullPath($Scratch)
if (Test-Path $Scratch) {
    throw "the scratch folder $Scratch exists already; name a new one"
}
$bin = Join-Path $Scratch 'bin'
$data = Join-Path $Scratch 'data'
$logs = Join-Path $Scratch 'logs'
$socket = Join-Path $data 'node.sock'

if (-not $NoBuild) {
    New-Item -ItemType Directory -Force $logs | Out-Null
    $buildLog = Join-Path $logs 'build.log'
    Write-Host 'building TianWen.UI.Gui (Debug), with tianwen-server beside it...'
    # A file for the output and no node reuse: a build on a pipe can wait out the worker nodes that inherited it.
    $build = Start-Process dotnet -ArgumentList 'build', (Join-Path $src 'TianWen.UI.Gui/TianWen.UI.Gui.csproj'), '-c', 'Debug', '-nologo', '-nodeReuse:false' `
        -RedirectStandardOutput $buildLog -RedirectStandardError (Join-Path $logs 'build-stderr.log') -NoNewWindow -PassThru
    $build.WaitForExit()
    if ($build.ExitCode -ne 0) {
        Get-Content $buildLog | Where-Object { $_ -match ': error ' } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        Write-Host "build FAILED (exit $($build.ExitCode))"
        exit 2
    }
}
foreach ($exe in 'tianwen-gui.exe', 'tianwen-server.exe') {
    if (-not (Test-Path (Join-Path $guiOut $exe))) { throw "no $exe in $guiOut; build TianWen.UI.Gui in Debug first" }
}

New-Item -ItemType Directory -Force $data, $logs | Out-Null
Write-Host "copying $guiOut to $bin..."
Copy-Item -Recurse $guiOut $bin
foreach ($name in $OmitNative) {
    Get-ChildItem $bin -Recurse -File -Filter $name | ForEach-Object {
        Remove-Item $_.FullName
        Write-Host "left out $($_.FullName.Substring($bin.Length + 1))"
    }
}

# --- The node's API over its socket ---------------------------------------------------------------------------------
function Invoke-Node([string] $Method, [string] $Path) {
    $body = & $curl -s -m 10 -X $Method --unix-socket $socket "http://localhost$Path"
    if ($LASTEXITCODE -ne 0 -or -not $body) { return $null }
    try { return ($body | ConvertFrom-Json).response } catch { return $null }
}

# --- What is watched, and what it binds -----------------------------------------------------------------------------
$roles = @{}
$endpoints = [ordered]@{}

# The processes running from the scratch copy, by path, so nothing else on the machine is watched or stopped. Cheap enough
# to ask on every sample (a CIM query is not: about 0.7 s, against a discovery socket that lives about 2 s), so only a new
# server's command line is read through CIM, once, for its role.
function Get-Watched {
    $mine = @(Get-Process -Name 'tianwen-server', 'tianwen-gui' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($bin, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($p in $mine) {
        if (-not $roles.ContainsKey($p.Id)) {
            $roles[$p.Id] = if ($p.ProcessName -eq 'tianwen-gui') { 'gui' }
                elseif ((Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)").CommandLine -match '(^|\s)--keeper(\s|$)') { 'keeper' }
                else { 'node' }
        }
    }
    return @($mine | ForEach-Object { $_.Id })
}

$samples = 0
$firstSample = $null
$lastSample = $null

function Save-Endpoints {
    $script:samples++
    $script:firstSample = $script:firstSample ?? [DateTime]::UtcNow
    $script:lastSample = [DateTime]::UtcNow
    foreach ($id in Get-Watched) {
        foreach ($t in @(Get-NetTCPConnection -State Listen -OwningProcess $id -ErrorAction SilentlyContinue)) {
            $endpoints["$id|TCP|$($t.LocalAddress)|$($t.LocalPort)"] = [pscustomobject]@{ Pid = $id; Protocol = 'TCP listen'; Address = $t.LocalAddress; Port = $t.LocalPort }
        }
        foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $id -ErrorAction SilentlyContinue)) {
            $endpoints["$id|UDP|$($u.LocalAddress)|$($u.LocalPort)"] = [pscustomobject]@{ Pid = $id; Protocol = 'UDP'; Address = $u.LocalAddress; Port = $u.LocalPort }
        }
    }
}

function Wait-Sampling([scriptblock] $Until, [int] $Seconds, [string] $What) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Save-Endpoints
        if ($Until -and (& $Until)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    if ($What) { Write-Host "timed out after $Seconds s waiting for $What" }
    return -not $Until
}

function Invoke-Discovery([string] $Label) {
    $job = Invoke-Node POST '/api/v1/devices/discover'
    if (-not $job) { Write-Host "$Label discovery: the node did not take it"; return }
    $script:jobState = $null
    [void](Wait-Sampling -Seconds 240 -What "$Label discovery" -Until {
        $script:jobState = Invoke-Node GET "/api/v1/jobs/$($job.id)"
        $script:jobState -and $script:jobState.state -ne 0
    })
    $states = 'Running', 'Succeeded', 'Failed', 'Cancelled'
    if ($script:jobState) {
        Write-Host "$Label discovery: $($states[$script:jobState.state]): $($script:jobState.step)$(if ($script:jobState.error) { " ($($script:jobState.error))" })"
    }
}

# --- Run ------------------------------------------------------------------------------------------------------------
$saved = @{}
foreach ($name in 'TIANWEN_DATA_ROOT', 'TIANWEN_LOCAL_ONLY', 'TIANWEN_GUI_SINGLE_INSTANCE', 'TIANWEN_NODE_SOCKET') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:TIANWEN_DATA_ROOT = $data
$env:TIANWEN_LOCAL_ONLY = '1'
$env:TIANWEN_GUI_SINGLE_INSTANCE = '0'
$env:TIANWEN_NODE_SOCKET = $null

$started = [System.Collections.Generic.List[int]]::new()

# The run itself: 0 when every endpoint is on loopback, 1 when one is not, 2 when the run could not get that far.
function Invoke-Check {
    $server = Start-Process (Join-Path $bin 'tianwen-server.exe') -WorkingDirectory $bin -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'server-stdout.log') -RedirectStandardError (Join-Path $logs 'server-stderr.log')
    $started.Add($server.Id)
    Write-Host "tianwen-server started, pid $($server.Id), data root $data"

    if (-not (Wait-Sampling -Seconds 60 -What 'the node to answer on its socket' -Until { $null -ne (Invoke-Node GET '/api/v1/node') })) {
        Get-Content (Join-Path $logs 'server-stderr.log') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
        return 2
    }
    $node = Invoke-Node GET '/api/v1/node'
    Write-Host "node $($node.processId): localOnly=$($node.localOnly), isShared=$($node.isShared)"

    Invoke-Discovery 'first'

    $gui = Start-Process (Join-Path $bin 'tianwen-gui.exe') -WorkingDirectory $bin -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'gui-stdout.log') -RedirectStandardError (Join-Path $logs 'gui-stderr.log')
    $started.Add($gui.Id)
    Write-Host "tianwen-gui started, pid $($gui.Id)"
    if ($Placement -eq 'Left') {
        Move-ToLeftHalf $gui
    }

    $attached = Wait-Sampling -Seconds 120 -What 'the GUI to attach to a node' -Until {
        if ($gui.HasExited) { return $true }
        $n = Invoke-Node GET '/api/v1/node'
        $n -and $n.clientsAttached -ge 1
    }
    if ($gui.HasExited) {
        Write-Host "the GUI EXITED, code $($gui.ExitCode); its stderr:"
        Get-Content (Join-Path $logs 'gui-stderr.log') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
        return 2
    }
    if ($Placement -eq 'Left') {
        # Again once it is attached: a window that sized or placed itself while it started is put back.
        Move-ToLeftHalf $gui
    }
    $node = Invoke-Node GET '/api/v1/node'
    if ($node) {
        Write-Host "the GUI is $(if ($attached) { 'attached to' } else { 'NOT attached to' }) node $($node.processId)$(if ($node.processId -ne $server.Id) { ' (not the one this script started: the GUI started its own)' }): localOnly=$($node.localOnly), isShared=$($node.isShared), clients=$($node.clientsAttached)"
        if ($node.processId -notin $started) { $started.Add([int]$node.processId) }
    }

    Invoke-Discovery 'second'
    [void](Wait-Sampling -Seconds $SettleSeconds)

    # --- Report -------------------------------------------------------------------------------------------------------
    $violations = 0
    Write-Host ''
    $span = ($lastSample - $firstSample).TotalMilliseconds
    Write-Host ("Endpoints seen (the union of {0} samples over {1:0} s, one every {2:0} ms):" -f $samples, ($span / 1000), ($span / [Math]::Max(1, $samples - 1)))
    $ids = @($roles.Keys | Sort-Object)
    foreach ($id in $ids) {
        Write-Host ("  {0} pid {1}" -f $roles[$id], $id)
        $mine = @($endpoints.Values | Where-Object Pid -eq $id | Sort-Object Protocol, Port)
        if ($mine.Count -eq 0) { Write-Host '    (none)' }
        foreach ($e in $mine) {
            $loopback = $e.Address -in '127.0.0.1', '::1'
            if (-not $loopback) { $violations++ }
            Write-Host ("    {0,-10} {1}:{2}{3}" -f $e.Protocol, $e.Address, $e.Port, $(if ($loopback) { '' } else { '   NOT LOOPBACK' }))
        }
    }
    Write-Host ''
    if ($violations -gt 0) {
        Write-Host "FAIL: $violations endpoint(s) on an address that is not loopback"
        return 1
    }
    Write-Host "PASS: $($endpoints.Count) endpoint(s) over $($ids.Count) process(es), every one on loopback"
    return 0
}

$exitCode = 2
try {
    $exitCode = Invoke-Check
}
finally {
    # The GUI first (a window has nothing to save here), then the node through its own shutdown, then anything left, by pid.
    foreach ($id in @($roles.Keys | Where-Object { $roles[$_] -eq 'gui' })) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    [void](Invoke-Node POST '/api/v1/node/shutdown')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and @(Get-Watched).Count -gt 0) { Start-Sleep -Milliseconds 250 }
    foreach ($id in @(Get-Watched) + $started) {
        if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            Write-Host "stopped pid $id"
        }
    }
    $left = @(Get-Watched)
    Write-Host $(if ($left.Count -eq 0) { 'every process it started has exited' } else { "STILL RUNNING: $($left -join ', ')" })

    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }

    if ($KeepScratch) {
        Write-Host "scratch kept: $Scratch (logs in $logs, the node's own under $data/Logs)"
    }
    else {
        Remove-Item -Recurse -Force $Scratch -ErrorAction SilentlyContinue
    }
}
exit $exitCode
