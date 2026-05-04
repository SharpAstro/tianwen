<#
.SYNOPSIS
Convert a SIMBAD JSON or NGC CSV catalog (lzip-compressed) into the
ASCII-separated text format consumed by AsciiRecordReader at runtime,
lzip-compressed as .gs.lz.

.DESCRIPTION
Reads a `*.json.lz` (SIMBAD shape) or `*.csv.lz` (NGC shape) and re-emits a
flat byte stream using:
  0x1D (GS) between records
  0x1E (RS) between fields within a record
  0x1F (US) between sub-items inside a variable-length field

Schema is auto-detected from the input file extension.

SIMBAD field order (`*.json.lz`):
  MainId | ObjType | Ra | Dec | VMag | BMinusV | Ids(joined by US)

NGC field order (`*.csv.lz`, only the columns the runtime actually reads):
  Name | Type | RA | Dec | Const | VMag | SurfBr | MajAx | MinAx | PosAng |
  M | NGC | IC | CommonNames(US-joined) | Identifiers(US-joined)

Numbers in SIMBAD are emitted in invariant culture, doubles via G17 to
guarantee round-trip. NGC numerics are kept as their original CSV string
form (Half.TryParse handles them at read time). Nullable / empty cells are
emitted as the empty string.

The output is written via lzip -9 to <Output>, e.g.:
  HR.json.lz  -> HR.gs.lz
  NGC.csv.lz  -> NGC.gs.lz

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

$invariant = [System.Globalization.CultureInfo]::InvariantCulture

# Defensive: refuse to emit a string that contains an ASCII separator byte. The
# whole point of the format is that GS/RS/US never appear in field content.
function Assert-NoSep([string] $s, [string] $context) {
    if ($null -ne $s -and $s.IndexOfAny([char[]]($GS, $RS, $US)) -ge 0) {
        throw "$context contains a separator byte in value '$s' -- refusing to emit."
    }
}

# Append a record (array of field strings) onto $sb with GS prefix on all but
# the first record. Field strings are emitted verbatim (caller ensures no
# separator bytes leak in). Sub-arrays should be pre-joined by the caller.
function Append-Record([System.Text.StringBuilder] $sb, [bool] $isFirst, [string[]] $fields) {
    if (-not $isFirst) { [void]$sb.Append($GS) }
    for ($i = 0; $i -lt $fields.Length; $i++) {
        if ($i -gt 0) { [void]$sb.Append($RS) }
        [void]$sb.Append($fields[$i])
    }
}

# Encode a SIMBAD .json.lz into the ASCII-separated stream. Returns the
# number of records emitted plus the raw text length used for size logging.
function Encode-Simbad([string] $InputPath, [System.Text.StringBuilder] $sb) {
    $tempJson = [System.IO.Path]::GetTempFileName() + '.json'
    try {
        & lzip -dc -- $InputPath | Set-Content -LiteralPath $tempJson -Encoding utf8NoBOM
        if ($LASTEXITCODE -ne 0) { throw "lzip -dc failed for $InputPath (exit $LASTEXITCODE)" }

        $jsonText = [System.IO.File]::ReadAllText($tempJson)
        $records = $jsonText | ConvertFrom-Json
        if ($null -eq $records) { throw "Parsed zero records from $InputPath" }
        if ($records -isnot [System.Array]) { $records = @($records) }

        $first = $true
        foreach ($rec in $records) {
            $mainId  = if ($null -ne $rec.MainId)  { [string]$rec.MainId }  else { '' }
            $objType = if ($null -ne $rec.ObjType) { [string]$rec.ObjType } else { '' }
            $ra      = ([double]$rec.Ra).ToString('G17', $invariant)
            $dec     = ([double]$rec.Dec).ToString('G17', $invariant)
            $vmag    = if ($null -ne $rec.VMag)    { ([double]$rec.VMag).ToString('G17', $invariant) }    else { '' }
            $bmv     = if ($null -ne $rec.BMinusV) { ([double]$rec.BMinusV).ToString('G17', $invariant) } else { '' }

            $idsList = @()
            if ($null -ne $rec.Ids) {
                foreach ($id in $rec.Ids) {
                    if ($null -eq $id) { continue }
                    $s = [string]$id
                    if ($s.Length -eq 0) { continue }
                    Assert-NoSep $s "Record '$mainId' Ids entry"
                    $idsList += $s
                }
            }
            $ids = [string]::Join($US, $idsList)

            Assert-NoSep $mainId  "Record '$mainId' MainId"
            Assert-NoSep $objType "Record '$mainId' ObjType"

            Append-Record $sb $first @($mainId, $objType, $ra, $dec, $vmag, $bmv, $ids)
            $first = $false
        }

        return [pscustomobject]@{ Count = $records.Count; RawTextLength = $jsonText.Length }
    }
    finally {
        if (Test-Path $tempJson) { Remove-Item -LiteralPath $tempJson -Force -ErrorAction SilentlyContinue }
    }
}

# Encode an NGC .csv.lz (semicolon-delimited, RFC 4180 quoting) into the
# ASCII-separated stream. Only the 15 columns MergeLzCsvData reads at runtime
# are emitted; everything else is dropped at build time.
function Encode-Ngc([string] $InputPath, [System.Text.StringBuilder] $sb) {
    $tempCsv = [System.IO.Path]::GetTempFileName() + '.csv'
    try {
        & lzip -dc -- $InputPath | Set-Content -LiteralPath $tempCsv -Encoding utf8NoBOM
        if ($LASTEXITCODE -ne 0) { throw "lzip -dc failed for $InputPath (exit $LASTEXITCODE)" }

        $csvText = [System.IO.File]::ReadAllText($tempCsv)
        # Import-Csv handles ';' delimiter and "..."-quoted fields with embedded ';' or commas.
        $records = Import-Csv -LiteralPath $tempCsv -Delimiter ';'
        if ($null -eq $records) { throw "Parsed zero rows from $InputPath" }
        if ($records -isnot [System.Array]) { $records = @($records) }

        # Helper: pull a field by header name; null/missing -> empty string.
        $get = { param($rec, $col) $v = $rec.$col; if ($null -eq $v) { '' } else { [string]$v } }

        # Helper: pre-split a comma-separated string field into US-joined tokens
        # (TrimEntries + RemoveEmpty matches MergeLzCsvData's runtime behaviour).
        $splitJoin = {
            param($rec, $col)
            $raw = & $get $rec $col
            if ($raw.Length -eq 0) { return '' }
            $parts = @()
            foreach ($p in $raw.Split(',')) {
                $t = $p.Trim()
                if ($t.Length -eq 0) { continue }
                Assert-NoSep $t "NGC '$($rec.Name)' $col entry"
                $parts += $t
            }
            return [string]::Join($US, $parts)
        }

        $first = $true
        $count = 0
        foreach ($rec in $records) {
            $name      = & $get $rec 'Name'
            if ($name.Length -eq 0) { continue }   # skip blank rows
            $type      = & $get $rec 'Type'
            $ra        = & $get $rec 'RA'
            $dec       = & $get $rec 'Dec'
            $constName = & $get $rec 'Const'
            $vmag      = & $get $rec 'V-Mag'
            $surfBr    = & $get $rec 'SurfBr'
            $majAx     = & $get $rec 'MajAx'
            $minAx     = & $get $rec 'MinAx'
            $posAng    = & $get $rec 'PosAng'
            $messier   = & $get $rec 'M'
            $ngc       = & $get $rec 'NGC'
            $ic        = & $get $rec 'IC'
            $commons   = & $splitJoin $rec 'Common names'
            $identif   = & $splitJoin $rec 'Identifiers'

            foreach ($pair in @(
                @('Name', $name), @('Type', $type), @('RA', $ra), @('Dec', $dec),
                @('Const', $constName), @('V-Mag', $vmag), @('SurfBr', $surfBr),
                @('MajAx', $majAx), @('MinAx', $minAx), @('PosAng', $posAng),
                @('M', $messier), @('NGC', $ngc), @('IC', $ic))) {
                Assert-NoSep $pair[1] "NGC '$name' $($pair[0])"
            }

            Append-Record $sb $first @(
                $name, $type, $ra, $dec, $constName,
                $vmag, $surfBr, $majAx, $minAx, $posAng,
                $messier, $ngc, $ic,
                $commons, $identif)
            $first = $false
            $count++
        }

        return [pscustomobject]@{ Count = $count; RawTextLength = $csvText.Length }
    }
    finally {
        if (Test-Path $tempCsv) { Remove-Item -LiteralPath $tempCsv -Force -ErrorAction SilentlyContinue }
    }
}

# Dispatch by input extension.
$sb = [System.Text.StringBuilder]::new(8 * 1024 * 1024)
$lower = $InputPath.ToLowerInvariant()
$summary =
    if ($lower.EndsWith('.json.lz')) { Encode-Simbad -InputPath $InputPath -sb $sb }
    elseif ($lower.EndsWith('.csv.lz')) { Encode-Ngc -InputPath $InputPath -sb $sb }
    else { throw "Unrecognized input extension on '$InputPath' (expected .json.lz or .csv.lz)." }

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
        -f (Split-Path -Leaf $InputPath), (Split-Path -Leaf $OutputPath), $summary.Count, $summary.RawTextLength, $rawSize, $inSize, $outSize)
}
finally {
    if (Test-Path $tempGs) { Remove-Item -LiteralPath $tempGs -Force -ErrorAction SilentlyContinue }
    if (Test-Path "$tempGs.lz") { Remove-Item -LiteralPath "$tempGs.lz" -Force -ErrorAction SilentlyContinue }
}
