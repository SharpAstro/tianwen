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
  Compress-FileToLz <path> [-MemberSize n]  managed compress at level 9; writes <path>.lz and
                    deletes the input -- the same contract as the old `lzip -9 <path>` shell-out.
                    -MemberSize is lzip's `-b` (independent members, 0 = one), which decides
                    whether LzipDecoder can parallelise the read at all.

No external lzip binary is needed anywhere (Lzip.Lib ships both the decoder AND the encoder since
#75/#76). That sentence used to be here as a claim and was false for three of the catalog-fetch
scripts, which still shelled out to `lzip` -- Get-Tycho2Catalogs, Get-VizierDobashi and
Get-VizierDarkNebulaShapes. They are converted; keep it that way, and note that these scripts run by
hand on a catalog refresh, so nothing in CI would have caught the drift.
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

# Compress $Path to "$Path.lz" at level 9 and delete the input file -- the same contract as the old
# external `lzip -9 <path>`.
#
# -MemberSize is the managed equivalent of lzip's `-b`: uncompressed bytes per INDEPENDENT member,
# 0 (the default) meaning one member. It is not cosmetic. LzipDecoder only parallelises across
# members, so a single-member catalog decodes serially however many cores are present -- which is
# why tyc2.bin.lz has always been baked at 4 MiB blocks. A conversion that quietly dropped it would
# have looked byte-clean and cost every desktop start-up its parallel decode.
function Compress-FileToLz([string] $Path, [long] $MemberSize = 0) {
    Initialize-Lzip
    $plain = [System.IO.File]::ReadAllBytes($Path)
    $options = if ($MemberSize -gt 0) {
        [SharpAstro.Lzip.LzipOptions] @{ MemberSize = $MemberSize }
    } else {
        [SharpAstro.Lzip.LzipOptions]::Default
    }
    $compressed = [SharpAstro.Lzip.LzipEncoder]::Compress($plain, $options)
    [System.IO.File]::WriteAllBytes("$Path.lz", $compressed)
    Remove-Item -LiteralPath $Path -Force
}
