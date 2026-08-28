<#
.SYNOPSIS
Expand the lzip-compressed Tycho-2 catalog into the flat, directly-mappable form that
TianWen.Lib embeds.

.DESCRIPTION
`tyc2.bin` is already the format the runtime wants: a 4-byte stream count, a per-GSC-region
offset table, then region-major 17-byte star records that `Tycho2RaDecIndex` and
`CopyTycho2Stars` read straight out of a flat span. The only thing standing between a solve
and its ~59 KB of records is the CONTAINER: lzip members are ~4 MiB and must be decoded whole,
so reaching any one region costs decompressing all 43.5 MB.

Embedding the expanded file instead makes the resource seekable over the mapped assembly image
(`GetManifestResourceStream` returns an `UnmanagedMemoryStream`), so a region query reads only
the pages it touches and init stops paying a decompression at all.

.NOTES
This runs at BUILD time and writes into `obj/`, deliberately, because the repository has no LFS
budget to spend: `.gitattributes` routes both `*.lz` AND a bare `tyc2.bin` to LFS, so the
expanded 43.5 MB must never land in the source tree where it could be committed. The committed
artifact stays `tyc2.bin.lz` exactly as before -- CI, clones and LFS usage are unchanged, and
this script is what turns it into the mappable form on the way past.
#>
param(
    [Parameter(Mandatory)][string] $LzPath,
    [Parameter(Mandatory)][string] $OutPath,
    [string] $LzipAssembly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/lzip-util.ps1"

Initialize-Lzip -LzipAssembly $LzipAssembly

$outDir = Split-Path -Parent $OutPath
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

Expand-LzToFile -LzPath $LzPath -OutPath $OutPath

$inSize = (Get-Item $LzPath).Length
$outSize = (Get-Item $OutPath).Length
Write-Host ("expand-tycho2: {0:N0} -> {1:N0} bytes ({2:N2}x)" -f $inSize, $outSize, ($outSize / $inSize))
