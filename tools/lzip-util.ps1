<#
.SYNOPSIS
Shared managed-lzip helpers (SharpAstro.Lzip / Lzip.Lib) for the catalog scripts.

.DESCRIPTION
Dot-source this from any script that reads or writes .lz files:

    . "$PSScriptRoot/lzip-util.ps1"          # from tools/
    . "$PSScriptRoot/../../../../tools/lzip-util.ps1"  # from src/TianWen.Lib/Astrometry/Catalogs/

Provides:
  Initialize-Lzip   [-LzipAssembly <path>]  load Lzip.Lib.dll once (explicit path ->
                    sibling-build probe -> NuGet global-packages probe)
  Expand-LzToFile   <in.lz> <out>           managed decompress (byte-verbatim)
  Compress-FileToLz <path>                  managed compress at level 9; writes <path>.lz and
                    deletes the input -- the same contract as the old `lzip -9 <path>` shell-out,
                    so no external lzip binary is needed anywhere anymore (Lzip.Lib ships both
                    the decoder AND the encoder since #75/#76).
#>

$script:LzipLoaded = $false

function Initialize-Lzip {
    param([string] $LzipAssembly)

    if ($script:LzipLoaded) { return }

    $dll = $null
    if ($LzipAssembly -and (Test-Path -LiteralPath $LzipAssembly)) {
        $dll = $LzipAssembly
    }
    else {
        # Fallbacks for standalone invocation (MSBuild supplies -LzipAssembly to preprocess-catalog).
        $candidates = @()
        # 1. Local sibling build output (UseLocalSiblings dev boxes): ../../Lzip.Lib/src/Lzip.Lib/bin.
        $siblingBin = Join-Path $PSScriptRoot '..\..\Lzip.Lib\src\Lzip.Lib\bin'
        if (Test-Path -LiteralPath $siblingBin) {
            $candidates += Get-ChildItem -LiteralPath $siblingBin -Recurse -Filter 'Lzip.Lib.dll' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending
        }
        # 2. NuGet global-packages cache (CI + package consumers): lzip.lib/<ver>/lib/netX/Lzip.Lib.dll.
        $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
        $lzipPkg = Join-Path $nugetRoot 'lzip.lib'
        if (Test-Path -LiteralPath $lzipPkg) {
            $candidates += Get-ChildItem -LiteralPath $lzipPkg -Recurse -Filter 'Lzip.Lib.dll' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]lib[\\/]net' } | Sort-Object FullName -Descending
        }
        $dll = ($candidates | Select-Object -First 1).FullName
    }

    if (-not $dll -or -not (Test-Path -LiteralPath $dll)) {
        throw "Could not locate Lzip.Lib.dll. Pass -LzipAssembly <path>, build the Lzip.Lib sibling, or restore the Lzip.Lib package."
    }

    Add-Type -LiteralPath $dll
    $script:LzipLoaded = $true
}

# Decompress an lzip (.lz) file to $OutPath using the managed decoder. Writes the decoded bytes
# verbatim (catalog payloads are already UTF-8 JSON/CSV), so there is no encoding round-trip.
function Expand-LzToFile([string] $LzPath, [string] $OutPath) {
    Initialize-Lzip
    $compressed = [System.IO.File]::ReadAllBytes($LzPath)
    $plain = [SharpAstro.Lzip.LzipDecoder]::Decompress($compressed)
    [System.IO.File]::WriteAllBytes($OutPath, $plain)
}

# Compress $Path to "$Path.lz" at level 9 (LzipOptions.Default) and delete the input file --
# byte-for-byte the same contract as the old external `lzip -9 <path>`.
function Compress-FileToLz([string] $Path) {
    Initialize-Lzip
    $plain = [System.IO.File]::ReadAllBytes($Path)
    $compressed = [SharpAstro.Lzip.LzipEncoder]::Compress($plain)
    [System.IO.File]::WriteAllBytes("$Path.lz", $compressed)
    Remove-Item -LiteralPath $Path -Force
}
