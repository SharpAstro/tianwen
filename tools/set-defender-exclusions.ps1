<#
.SYNOPSIS
    Shows Microsoft Defender's exclusions and adds the ones an imaging machine running TianWen needs. Run it as
    often as you like: it only adds what is missing and never removes anything.

.DESCRIPTION
    Real-time scanning gets in the way of a FITS workflow on two sides: a capture program writing frames (a SER
    video is gigabytes), and a viewer or stacker reading them back. What it adds:

    Always:
      - the image extensions TianWen opens (.fit .fits .fts .fz .ser, FileAssociationRegistrar's list). An
        extension exclusion holds on EVERY drive, which is fine for data-only formats. .gz is deliberately
        absent: Defender matches the LAST extension, so excluding it for .fits.gz would exclude every .gz.
      - TianWen's own programs, by process: every Microsoft Store package of SharpAstro's
        (WindowsApps\SharpAstro.*, Astro Photo Viewer among them), and the release binaries by name, since a
        release .tar.gz is extracted wherever its user likes.
      - TianWen's data folder (%LOCALAPPDATA%\TianWen: catalogues, logs, profiles).
    With -CaptureFolder: each folder named, subfolders included. A process exclusion covers what a program READS;
      only a folder exclusion also covers the capture program WRITING there.
    With -Dev: the build and test side. The source folder (as a folder AND as a process path, so every test host
      and Debug app built there is covered wherever it opens files), the NuGet caches, the dotnet install, the
      test output roots under %TEMP%, and the build processes by name (dotnet, MSBuild, VBCSCompiler, testhost,
      git, git-lfs).

    Defender shows its exclusions only to an elevated session, so the script relaunches itself elevated (one UAC
    prompt) in a window that stays open; read the result there.

.PARAMETER CaptureFolder
    Folders a capture program writes into (C:\Captures, say). Repeatable: -CaptureFolder C:\Captures,D:\Astro

.PARAMETER Dev
    Also exclude the build tool chain and the source folder.

.PARAMETER SourceFolder
    The folder holding the clones, for -Dev. Defaults to the folder this repository sits in (its siblings
    beside it).

.PARAMETER ShowOnly
    List what is there and what is missing, change nothing.

.EXAMPLE
    pwsh tools/set-defender-exclusions.ps1 -CaptureFolder C:\Captures -ShowOnly
.EXAMPLE
    pwsh tools/set-defender-exclusions.ps1 -CaptureFolder C:\Captures -Dev
#>
[CmdletBinding()]
param(
    [string[]]$CaptureFolder = @(),
    [switch]$Dev,
    [string]$SourceFolder = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch]$ShowOnly
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $relaunch = @('-NoProfile', '-NoExit', '-File', $PSCommandPath)
    if ($CaptureFolder.Count) { $relaunch += '-CaptureFolder'; $relaunch += ($CaptureFolder -join ',') }
    if ($Dev) { $relaunch += '-Dev'; $relaunch += '-SourceFolder'; $relaunch += "`"$SourceFolder`"" }
    if ($ShowOnly) { $relaunch += '-ShowOnly' }
    Write-Host 'Defender needs an elevated session; relaunching (accept the UAC prompt, the result is in the new window).'
    Start-Process pwsh -Verb RunAs -ArgumentList $relaunch
    return
}

$tianwenApps = 'tianwen', 'tianwen-server', 'tianwen-gui', 'tianwen-fits', 'tianwen-mcp', 'tianwen-ascomhost' |
    ForEach-Object { "$_.exe" }

$want = @{
    Path      = @(Join-Path $env:LOCALAPPDATA 'TianWen') + $CaptureFolder
    Extension = @('.fit', '.fits', '.fts', '.fz', '.ser')
    # The wildcard spans the versioned package folder, so a Store update keeps the exclusion.
    Process   = @("$env:ProgramFiles\WindowsApps\SharpAstro.*\*.exe") + $tianwenApps
}

if ($Dev) {
    $want.Path += @(
        $SourceFolder
        (Join-Path $env:USERPROFILE '.nuget\packages')
        (Join-Path $env:LOCALAPPDATA 'NuGet')
        (Join-Path $env:ProgramFiles 'dotnet')
        # Test output: TempFolders' common root (unit, functional and GPU suites), FC.SDK.Raw's, the benchmarks'.
        # Tests that still make a folder of their own straight under %TEMP% are covered by the source folder's
        # process exclusion below, since every test host runs from its build output there.
        (Join-Path $env:TEMP 'TianWen.Lib.Tests')
        (Join-Path $env:TEMP 'FC.SDK.Raw.Tests')
        (Join-Path $env:TEMP 'TianWen.UI.Benchmarks')
    )
    # "<folder>\*" as a process exclusion covers files opened by any program whose image lives under the folder.
    # git and git-lfs: a status in this repository (LFS, a large tree) runs 250-300 ms on an N100, and every
    # Claude Code status-line refresh runs one.
    $want.Process += @("$SourceFolder\*", 'dotnet.exe', 'MSBuild.exe', 'VBCSCompiler.exe', 'testhost.exe',
        'git.exe', 'git-lfs.exe')
}

foreach ($folder in $CaptureFolder) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        Write-Warning "Capture folder '$folder' does not exist (excluded anyway, so it is covered once created)."
    }
}

$pref = Get-MpPreference
$have = @{
    Path      = @($pref.ExclusionPath | Where-Object { $_ })
    Extension = @($pref.ExclusionExtension | Where-Object { $_ })
    Process   = @($pref.ExclusionProcess | Where-Object { $_ })
}

$missingCount = 0
foreach ($kind in 'Path', 'Extension', 'Process') {
    Write-Host "`n== $kind ==" -ForegroundColor Cyan
    foreach ($h in $have[$kind]) { Write-Host "  have:    $h" }
    foreach ($w in ($want[$kind] | Select-Object -Unique)) {
        # Defender compares exclusions case-insensitively; so does -contains.
        if ($have[$kind] -contains $w) { continue }
        $missingCount++
        if ($ShowOnly) { Write-Host "  missing: $w" -ForegroundColor Yellow; continue }
        $splat = @{ "Exclusion$kind" = $w }
        try {
            Add-MpPreference @splat
            Write-Host "  added:   $w" -ForegroundColor Green
        } catch {
            Write-Host "  FAILED:  $w ($($_.Exception.Message))" -ForegroundColor Red
        }
    }
}

Write-Host ''
if ($missingCount -eq 0) { Write-Host 'Nothing missing.' -ForegroundColor Green }
elseif ($ShowOnly) { Write-Host "$missingCount missing; run again without -ShowOnly to add them." -ForegroundColor Yellow }
