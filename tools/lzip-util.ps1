<#
.SYNOPSIS
Shared managed-lzip helpers (SharpAstro.Lzip / Lzip.Lib) for the catalog scripts.

.DESCRIPTION
Dot-source this from any script that reads or writes .lz files:

    . "$PSScriptRoot/lzip-util.ps1"          # from tools/
    . "$PSScriptRoot/../../../../tools/lzip-util.ps1"  # from src/TianWen.Lib/Astrometry/Catalogs/

Provides:
  Initialize-Lzip   [-LzipAssembly <path>]  load Lzip.Lib.dll once (explicit path ->
                    sibling-build probe -> NuGet global-packages probe), and every assembly it
                    references that the runtime does not carry (Import-AssemblyReferences)
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

# The newest version folder under a NuGet package's cache folder, compared as VERSIONS: as text,
# 1.1.91 sorts above 1.1.111. A folder whose name is not a plain version (a prerelease) sorts last.
function Get-NewestPackageVersionFolder([string] $PackageFolder) {
    Get-ChildItem -LiteralPath $PackageFolder -Directory -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { $v = $null; if ([version]::TryParse($_.Name, [ref] $v)) { $v } else { [version] '0.0' } } } -Descending |
        Select-Object -First 1
}

# Add-Type loads ONE file, and pwsh resolves an assembly reference only from what it already holds,
# never from the NuGet cache. So every reference of $Dll the runtime cannot load itself is loaded
# first: from beside $Dll (a build output that copied it), else from the newest version of the
# package of that name in $NugetRoot, at the highest lib/netX.Y this runtime can run. Lzip.Lib
# 1.1.111 (2026-10-10) took System.IO.Hashing for its trailer check, and every build that expanded
# a catalogue failed on it with "Could not load file or assembly 'System.IO.Hashing'".
function Import-AssemblyReferences([string] $Dll, [string] $NugetRoot) {
    $stream = [System.IO.File]::OpenRead($Dll)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $names = foreach ($handle in $md.AssemblyReferences) { $md.GetString($md.GetAssemblyReference($handle).Name) }
    }
    finally {
        $stream.Dispose()
    }
    $runtimeMajor = [Environment]::Version.Major
    foreach ($name in $names) {
        if ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq $name }) { continue }
        try {
            [void][System.Reflection.Assembly]::Load([System.Reflection.AssemblyName]::new($name))
            continue # the runtime carries it
        }
        catch [System.IO.FileNotFoundException] {
        }
        $found = Join-Path (Split-Path -Parent $Dll) "$name.dll"
        if (-not (Test-Path -LiteralPath $found)) {
            $found = $null
            $newest = Get-NewestPackageVersionFolder (Join-Path $NugetRoot $name.ToLowerInvariant())
            if ($newest) {
                $lib = Get-ChildItem -LiteralPath (Join-Path $newest.FullName 'lib') -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '^net(\d+)\.\d+$' -and [int] $Matches[1] -le $runtimeMajor } |
                    Sort-Object @{ Expression = { [version] ($_.Name -replace '^net', '') } } -Descending |
                    Select-Object -First 1
                if ($lib -and (Test-Path -LiteralPath (Join-Path $lib.FullName "$name.dll"))) {
                    $found = Join-Path $lib.FullName "$name.dll"
                }
            }
        }
        if (-not $found) {
            throw "$(Split-Path -Leaf $Dll) references $name, which neither sits beside it nor is in $NugetRoot. Restore the package that brings it."
        }
        Add-Type -LiteralPath $found
    }
}

function Initialize-Lzip {
    param([string] $LzipAssembly)

    if ($script:LzipLoaded) { return }

    $dll = $null
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
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
        # 2. NuGet global-packages cache (CI + package consumers): lzip.lib/<ver>/lib/netX/Lzip.Lib.dll,
        #    from the newest version.
        $newest = Get-NewestPackageVersionFolder (Join-Path $nugetRoot 'lzip.lib')
        if ($newest) {
            $candidates += Get-ChildItem -LiteralPath $newest.FullName -Recurse -Filter 'Lzip.Lib.dll' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '[\\/]lib[\\/]net' } | Sort-Object FullName -Descending
        }
        $dll = ($candidates | Select-Object -First 1).FullName
    }

    if (-not $dll -or -not (Test-Path -LiteralPath $dll)) {
        throw "Could not locate Lzip.Lib.dll. Pass -LzipAssembly <path>, build the Lzip.Lib sibling, or restore the Lzip.Lib package."
    }

    Import-AssemblyReferences -Dll $dll -NugetRoot $nugetRoot
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
