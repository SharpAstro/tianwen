<#
.SYNOPSIS
Convert a SIMBAD JSON catalog (lzip-compressed) into the ASCII-separated text
format consumed by AsciiRecordReader at runtime, lzip-compressed as .gs.lz.

.DESCRIPTION
Reads a `*.json.lz` produced by Get-SimbadCatalogs.ps1, parses each record, and
re-emits a flat byte stream using:
  0x1D (GS) between records
  0x1E (RS) between fields within a record
  0x1F (US) between sub-items inside a variable-length field (Ids[])

Field order for SIMBAD records:
  MainId | ObjType | Ra | Dec | VMag | BMinusV | Ids(joined by US)

Numbers are emitted in invariant culture, doubles via G17 to guarantee round-trip.
Nullable fields (VMag, BMinusV) are emitted as the empty string.

The output is written via lzip -9 to <Output>, e.g. HR.json.lz -> HR.gs.lz.

.EXAMPLE
pwsh -NoProfile -File tools/preprocess-catalog.ps1 `
    -Input src/TianWen.Lib/Astrometry/Catalogs/HR.json.lz `
    -Output src/TianWen.Lib/Astrometry/Catalogs/HR.gs.lz
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias('Input')] [string] $InputPath,
    [Parameter(Mandatory)] [Alias('Output')] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

# ASCII control codes used as separators. These bytes do not appear in any
# SIMBAD/NGC field value, so no escaping is ever needed.
$GS = [char]0x1D   # group/record separator
$RS = [char]0x1E   # record/field separator
$US = [char]0x1F   # unit/sub-item separator

if (-not (Test-Path $InputPath)) {
    throw "Input file not found: $InputPath"
}

# Decompress .json.lz to a temp .json. lzip -dc writes to stdout.
$tempJson = [System.IO.Path]::GetTempFileName() + '.json'
try {
    & lzip -dc -- $InputPath | Set-Content -LiteralPath $tempJson -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw "lzip -dc failed for $InputPath (exit $LASTEXITCODE)" }

    # ConvertFrom-Json on a bare array returns a [PSCustomObject[]] in pwsh 7+.
    $jsonText = [System.IO.File]::ReadAllText($tempJson)
    $records = $jsonText | ConvertFrom-Json
    if ($null -eq $records) { throw "Parsed zero records from $InputPath" }
    if ($records -isnot [System.Array]) { $records = @($records) }

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture

    $sb = [System.Text.StringBuilder]::new(8 * 1024 * 1024)
    $first = $true
    foreach ($rec in $records) {
        if (-not $first) { [void]$sb.Append($GS) }
        $first = $false

        $mainId  = if ($null -ne $rec.MainId)  { [string]$rec.MainId }  else { '' }
        $objType = if ($null -ne $rec.ObjType) { [string]$rec.ObjType } else { '' }
        $ra      = ([double]$rec.Ra).ToString('G17', $invariant)
        $dec     = ([double]$rec.Dec).ToString('G17', $invariant)
        $vmag    = if ($null -ne $rec.VMag)    { ([double]$rec.VMag).ToString('G17', $invariant) }    else { '' }
        $bmv     = if ($null -ne $rec.BMinusV) { ([double]$rec.BMinusV).ToString('G17', $invariant) } else { '' }

        # Ids is a string[]; join by US. May be $null on records that didn't survive the filter.
        $idsList = @()
        if ($null -ne $rec.Ids) {
            foreach ($id in $rec.Ids) {
                if ($null -eq $id) { continue }
                $s = [string]$id
                if ($s.Length -eq 0) { continue }
                # Defensive: strip control bytes that would corrupt slicing if SIMBAD ever emitted them.
                if ($s.IndexOfAny([char[]]($GS, $RS, $US)) -ge 0) {
                    throw "Record '$mainId' contains a separator byte in Ids entry '$s' — refusing to emit."
                }
                $idsList += $s
            }
        }
        $ids = [string]::Join($US, $idsList)

        # Defensive same-check on string fields.
        foreach ($f in @($mainId, $objType)) {
            if ($f.IndexOfAny([char[]]($GS, $RS, $US)) -ge 0) {
                throw "Record '$mainId' contains a separator byte in field '$f' — refusing to emit."
            }
        }

        [void]$sb.Append($mainId).Append($RS)
        [void]$sb.Append($objType).Append($RS)
        [void]$sb.Append($ra).Append($RS)
        [void]$sb.Append($dec).Append($RS)
        [void]$sb.Append($vmag).Append($RS)
        [void]$sb.Append($bmv).Append($RS)
        [void]$sb.Append($ids)
    }

    # Write UTF-8 (no BOM) to a temp .gs file, then lzip -9 to produce .gs.lz next to it.
    $tempGs = [System.IO.Path]::GetTempFileName() + '.gs'
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
        [System.IO.File]::WriteAllBytes($tempGs, $bytes)

        # lzip writes <input>.lz next to the input. Move it to $OutputPath.
        & lzip -9 -f -- $tempGs
        if ($LASTEXITCODE -ne 0) { throw "lzip -9 failed (exit $LASTEXITCODE)" }
        $producedLz = "$tempGs.lz"
        if (-not (Test-Path $producedLz)) { throw "lzip did not produce $producedLz" }

        $outDir = Split-Path -Parent $OutputPath
        if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
        Move-Item -LiteralPath $producedLz -Destination $OutputPath -Force

        $inSize  = (Get-Item -LiteralPath $InputPath).Length
        $rawSize = $bytes.Length
        $outSize = (Get-Item -LiteralPath $OutputPath).Length
        Write-Host ("preprocess-catalog: {0} -> {1}: {2:N0} records, {3:N0}->{4:N0} raw bytes, {5:N0}->{6:N0} LZ bytes" `
            -f (Split-Path -Leaf $InputPath), (Split-Path -Leaf $OutputPath), $records.Count, $jsonText.Length, $rawSize, $inSize, $outSize)
    }
    finally {
        if (Test-Path $tempGs) { Remove-Item -LiteralPath $tempGs -Force -ErrorAction SilentlyContinue }
        if (Test-Path "$tempGs.lz") { Remove-Item -LiteralPath "$tempGs.lz" -Force -ErrorAction SilentlyContinue }
    }
}
finally {
    if (Test-Path $tempJson) { Remove-Item -LiteralPath $tempJson -Force -ErrorAction SilentlyContinue }
}
