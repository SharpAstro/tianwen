<#
.SYNOPSIS
    The real GUI's planetary capture through the node on the attached ToupTek, recorded for a minute at the Saturn window:
    the evidence beside NodePlanetaryRateProbe's gate (TIA2, docs/plans/live-planetary-capture.md), which a person drives.

.DESCRIPTION
    Runs NodePlanetaryRateProbe.TheRealGuiWindowShowsTheCaptureLiveWhileTheNodeRecords in THIS console, which shows what
    it does and the clicks it needs:
    1. Builds TianWen.UI.Gui and the functional tests in Release (tianwen-server is built beside the tests), unless -NoBuild.
    2. Starts a local-only tianwen-server on a scratch data root and a socket of its own, holding the ToupTek in a profile,
       and connects the camera.
    3. Opens tianwen-gui, local-only, on that node, and places its window on the LEFT half of the screen.
    4. Asks the person at the desk, in this console, for the clicks the GUI takes no other way: the Live Session tab, the
       Planetary mode pill, Exp 5 ms, Gain 2252, ROI 640x480, 8 bit, Start. It waits until the node streams that.
    5. Jogs the window to the Saturn window (1716, 546), then asks the person for the Record control's Record (#1344), its
       Secs at -Seconds (60, the field's own until typed over). It waits until the node records, takes the window by
       PrintWindow halfway through the recording into -Out, and prints the SER's rate, the node's streams and every TCP
       listener and UDP endpoint of the node, the GUI and the test (all must be loopback).
    6. Closes the GUI and stops the node, each by its pid, and leaves the camera closed.

    The camera must be attached and held by no other program. The SER (about 3.6 GB) is deleted once read back.

.PARAMETER Out
    The folder for the PNG and this run's log; a new one under the temp folder by default.

.PARAMETER Seconds
    How long the recording lasts: what the person types into the Record control's Secs, and when its picture is taken.

.PARAMETER NoBuild
    Use the last Release builds as they are.

.PARAMETER CheckOnly
    Prove the launcher with nobody at the desk: the node, the GUI placed on the left half and attached to it, one PrintWindow
    picture of it and its endpoints, then everything closed by its pid. No capture, no recording.

.EXAMPLE
    pwsh -NoProfile -File tools/planetary-gui-evidence.ps1
#>
[CmdletBinding()]
param(
    [string] $Out = (Join-Path ([IO.Path]::GetTempPath()) ("tianwen-gui-evidence-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [int] $Seconds = 60,
    [switch] $NoBuild,
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'TianWen GUI evidence: the planetary capture through the node and its Record control (TIA2, #1344)'

$src = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$gui = Join-Path $src 'TianWen.UI.Gui/bin/Release/net10.0/tianwen-gui.exe'
$tests = Join-Path $src 'TianWen.Lib.Tests.Functional/bin/Release/net10.0/TianWen.Lib.Tests.Functional.exe'
New-Item -ItemType Directory -Force $Out | Out-Null

if (-not $NoBuild) {
    foreach ($project in 'TianWen.UI.Gui', 'TianWen.Lib.Tests.Functional') {
        Write-Host "building $project (Release)..."
        # A file for the output and no node reuse: a build on a pipe can wait out the worker nodes that inherited it.
        $log = Join-Path $Out "build-$project.log"
        $build = Start-Process dotnet -ArgumentList 'build', (Join-Path $src "$project/$project.csproj"), '-c', 'Release', '-nologo', '-nodeReuse:false' `
            -RedirectStandardOutput $log -RedirectStandardError (Join-Path $Out "build-$project-stderr.log") -NoNewWindow -PassThru
        $build.WaitForExit()
        if ($build.ExitCode -ne 0) {
            Get-Content $log | Where-Object { $_ -match ': error ' } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
            Write-Host "build of $project FAILED (exit $($build.ExitCode))"
            exit 2
        }
    }
}
foreach ($exe in $gui, $tests) {
    if (-not (Test-Path $exe)) { throw "no $exe; build it in Release first" }
}

$env:TIANWEN_TOUPTEK_RATE_PROBE = '1'
$env:TIANWEN_TOUPTEK_GUI_EVIDENCE = $(if ($CheckOnly) { 'check' } else { '1' })
$env:TIANWEN_GUI_UNDER_TEST = $gui
$env:TIANWEN_GUI_EVIDENCE_OUT = $Out
$env:TIANWEN_TOUPTEK_RECORD_SECONDS = "$Seconds"
$env:TIANWEN_LOCAL_ONLY = '1'

$log = Join-Path $Out 'evidence.log'
Write-Host "running the evidence run; its log is $log"
& $tests -method 'TianWen.Lib.Tests.Functional.NodePlanetaryRateProbe.TheRealGuiWindowShowsTheCaptureLiveWhileTheNodeRecords' -showLiveOutput -noColor |
    Tee-Object -FilePath $log
$code = $LASTEXITCODE
Write-Host ''
Write-Host "exit $code; the PNG and the log are in $Out"
exit $code
