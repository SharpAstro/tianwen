<#
.SYNOPSIS
    Build, verify the binary is not stale, then launch a detached dataset bake.

.DESCRIPTION
    Exists because a bake was once launched with `--no-build` against a binary built three hours
    and two commits earlier. It ran 100 minutes and 16 of 68 sessions before anyone noticed, and
    nothing about the run looked wrong: the archive scan was correct, the tiles were structurally
    complete, the manifest matched the files on disk exactly, and not one warning was emitted. It
    was caught only by comparing the produced masters' pixel statistics against the previous bake
    and finding them identical to six decimal places.

    So the staleness check does not belong in anyone's head. It belongs here, ahead of the launch,
    and it FAILS rather than warns.

    Also stamps the resolved commit + build time into the output directory, because the dataset
    itself records no provenance: a master carries no STACK_N and inherits the capture software's
    SWCREATE, so after the fact there is no way to ask a bake which code produced it.

.PARAMETER Out
    Dataset output directory. A bake-provenance.json is written here.

.PARAMETER ArchiveRoot
    One or more archive roots. Quote any path containing spaces.

.PARAMETER ScratchRoot
    Scratch directory, which should be on an SSD; reading lights is already spindle-bound.

.PARAMETER ExtraArgs
    Passed through to `dataset build` verbatim (e.g. --exclude-path, --resume, --force-psf).

.EXAMPLE
    ./tools/run-dataset-bake.ps1 -Out D:\Astro-Dataset\2025-2026 `
        -ArchiveRoot 'D:\Astro-Pics\2025','D:\Astro-Pics\2026' `
        -ScratchRoot C:\temp\astro-scratch `
        -ExtraArgs '--exclude-instrume','*simulator*'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Out,
    [Parameter(Mandatory)][string[]] $ArchiveRoot,
    [string] $ScratchRoot = 'C:\temp\astro-scratch',
    [string[]] $ExtraArgs = @(),
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
$exe = Join-Path $src 'TianWen.Cli\bin\Release\net10.0\tianwen.exe'

Push-Location $repo
try {
    $sha = (git rev-parse HEAD).Trim()
    $shaShort = (git rev-parse --short HEAD).Trim()
    # Commit DATE, not author date: a rebase rewrites the former and the binary can only have been
    # produced after the latter.
    $commitUtc = [datetime]::Parse((git show -s --format=%cI HEAD)).ToUniversalTime()
    $dirty = @(git status --porcelain -- 'src' '*.csproj' '*.props')

    Write-Host "HEAD $shaShort committed $($commitUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss'))"
    if ($dirty.Count -gt 0) {
        Write-Warning "$($dirty.Count) uncommitted change(s) under src/. The bake will run them; they are not recoverable from the SHA alone."
        $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
    }

    if (-not $SkipBuild) {
        Write-Host 'building Release ...'
        # A running bake holds a lock on tianwen.exe, so a build during one fails here rather than
        # half-writing the binary the next launch would use.
        dotnet build (Join-Path $src 'TianWen.Cli') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'build FAILED; refusing to launch' }
    }

    if (-not (Test-Path $exe)) { throw "no binary at $exe" }
    $item = Get-Item $exe
    $builtUtc = $item.LastWriteTimeUtc

    # The SDK's built-in source link appends the commit to AssemblyInformationalVersion, which
    # surfaces as the exe's Win32 ProductVersion, so the binary's OWN commit is readable without
    # running it. This is the real check; the timestamp comparison below only catches the case
    # where there is no source-control info to compare.
    $binarySha = ($item.VersionInfo.ProductVersion -split '\+')[-1]
    Write-Host "binary built $($builtUtc.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss')) from $($binarySha.Substring(0, [Math]::Min(8, $binarySha.Length)))"

    if ($binarySha -match '^[0-9a-f]{40}$') {
        if ($binarySha -ne $sha) {
            throw ("STALE BINARY: built from $($binarySha.Substring(0,8)) but HEAD is $shaShort. " +
                   'Re-run without -SkipBuild. Refusing to launch.')
        }
    }
    elseif ($builtUtc -lt $commitUtc) {
        # No usable SHA (a source drop, or a tree that is not a git checkout), so fall back to
        # times. Weaker: a touched file passes, and a matching mtime does not prove the binary was
        # built FROM this commit. Good enough as a backstop, never as the primary check.
        throw ("STALE BINARY: built $($builtUtc.ToLocalTime().ToString('HH:mm:ss')) but HEAD " +
               "$shaShort landed $($commitUtc.ToLocalTime().ToString('HH:mm:ss')), and the binary " +
               'carries no commit to compare. Re-run without -SkipBuild. Refusing to launch.')
    }

    New-Item -ItemType Directory -Force -Path $Out, $ScratchRoot | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $log = Join-Path ([IO.Path]::GetTempPath()) "astro-bake-$stamp.log"
    $err = Join-Path ([IO.Path]::GetTempPath()) "astro-bake-$stamp.err.log"

    # Start-Process joins ArgumentList on spaces without re-quoting, so a path containing a space
    # arrives as several arguments. Quote each one here.
    $q = { param($s) if ($s -match '\s') { '"' + $s + '"' } else { $s } }
    $argv = @('dataset', 'build', '--archive-root') +
            ($ArchiveRoot | ForEach-Object { & $q $_ }) +
            @('--out', (& $q $Out), '--scratch-root', (& $q $ScratchRoot)) +
            ($ExtraArgs | ForEach-Object { & $q $_ })

    $proc = Start-Process -FilePath $exe -ArgumentList $argv -WorkingDirectory $src `
        -RedirectStandardOutput $log -RedirectStandardError $err -WindowStyle Hidden -PassThru

    [ordered]@{
        commit       = $sha
        binaryCommit = $binarySha
        commitUtc    = $commitUtc.ToString('o')
        binaryBuilt  = $builtUtc.ToString('o')
        dirtyFiles   = $dirty
        startedUtc   = (Get-Date).ToUniversalTime().ToString('o')
        pid          = $proc.Id
        archiveRoots = $ArchiveRoot
        scratchRoot  = $ScratchRoot
        extraArgs    = $ExtraArgs
        stdout       = $log
        stderr       = $err
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'bake-provenance.json')

    Write-Host "launched PID $($proc.Id) on $shaShort"
    Write-Host "  stdout  $log"
    Write-Host "  stderr  $err"
    Write-Host "  provenance  $(Join-Path $Out 'bake-provenance.json')"
}
finally { Pop-Location }
