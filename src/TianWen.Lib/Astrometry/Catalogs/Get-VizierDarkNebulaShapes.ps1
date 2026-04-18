[CmdletBinding()]
param()

# Pulls per-object size information from VizieR source catalogs for the
# Simbad-derived *.json.lz families that otherwise ship without shape data
# (DarkNeb / RefNeb / etc). Writes side-car *.shapes.json.lz files next to
# the existing *.json.lz catalogs. Sky-map overlay code consumes these to
# draw clouds as natural-size ellipses, so we can drop the hard FOV>10deg
# cutoff that was only there to suppress undifferentiated clutter.
#
# Each entry is [{ Seq, Maj, Min, PA }] where Maj/Min are in arcmin and PA
# in degrees. Clouds without published ellipse are treated as circular
# (Maj = Min, PA = 0).

$tapUrl = 'https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync'

function Invoke-Vizier {
    param([string]$Adql)
    $body = @{ REQUEST='doQuery'; LANG='ADQL'; FORMAT='tsv'; QUERY=$Adql }
    $tsv = (Invoke-WebRequest -Method Post -Uri $tapUrl -Body $body -UseBasicParsing).Content
    $lines = $tsv -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }
    if ($lines.Count -lt 2) { throw "TAP returned no rows for: $Adql" }
    $header = $lines[0] -split "`t"
    $colIdx = @{}
    for ($i = 0; $i -lt $header.Count; $i++) { $colIdx[$header[$i]] = $i }
    [PSCustomObject]@{ Header = $header; ColIdx = $colIdx; Rows = $lines | Select-Object -Skip 1 }
}

function Save-Shapes {
    param([string]$Name, [object[]]$Entries)
    $outFile = "$PSScriptRoot/$Name.shapes.json"
    $lzFile  = "$outFile.lz"
    if (Test-Path $lzFile) { Remove-Item $lzFile }
    $Entries | ConvertTo-Json -Compress | Out-File -Encoding UTF8NoBOM $outFile
    $uncompressedSize = [int](Get-Item $outFile).Length
    $null = lzip -9 $outFile
    if (Test-Path $lzFile) {
        $compressedSize = (Get-Item $lzFile).Length
        $ratio = if ($uncompressedSize -gt 0) { $compressedSize / $uncompressedSize * 100 } else { 0 }
        Write-Host ("  {0}.shapes.json.lz: {1:N0} -> {2:N0} bytes ({3:N1}%), {4} entries" -f $Name, $uncompressedSize, $compressedSize, $ratio, $Entries.Count)
    }
}

# ----- Dobashi (J/PASJ/63/S1/table8): 7614 entries, Area in arcmin^2 -----
function Get-DobashiShapes {
    $q = Invoke-Vizier 'SELECT Seq, Area FROM "J/PASJ/63/S1/table8"'
    $iSeq  = $q.ColIdx['Seq']
    $iArea = $q.ColIdx['Area']
    $out = foreach ($line in $q.Rows) {
        $cols = $line -split "`t"
        $seqStr  = $cols[$iSeq].Trim('"', ' ')
        $areaStr = $cols[$iArea].Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($seqStr) -or [string]::IsNullOrWhiteSpace($areaStr)) { continue }
        $diam = 2.0 * [Math]::Sqrt([double]$areaStr / [Math]::PI)
        [PSCustomObject][ordered]@{ Seq = [int]$seqStr; Maj = [Math]::Round($diam, 2); Min = [Math]::Round($diam, 2); PA = 0 }
    }
    Save-Shapes 'Dobashi' @($out)
}

# ----- Barnard (VII/220A/barnard): 349 entries, Diam in arcmin -----
# Entries carry an optional lowercase-letter suffix for sub-condensations
# (44a, 67a, ...). Skip those — our CatalogIndex packing does not encode
# the suffix, so sub-entries fall back to the existing fixed-circle marker.
function Get-BarnardShapes {
    $q = Invoke-Vizier 'SELECT Barn, Diam FROM "VII/220A/barnard"'
    $iBarn = $q.ColIdx['Barn']
    $iDiam = $q.ColIdx['Diam']
    $out = foreach ($line in $q.Rows) {
        $cols = $line -split "`t"
        $barnStr = $cols[$iBarn].Trim('"', ' ')
        $diamStr = $cols[$iDiam].Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($barnStr) -or [string]::IsNullOrWhiteSpace($diamStr)) { continue }
        if ($barnStr -notmatch '^\d+$') { continue }  # skip a/b sub-entries
        $diam = [double]$diamStr
        [PSCustomObject][ordered]@{ Seq = [int]$barnStr; Maj = [Math]::Round($diam, 2); Min = [Math]::Round($diam, 2); PA = 0 }
    }
    Save-Shapes 'Barnard' @($out)
}

# ----- LDN (VII/7A/ldn): 1802 entries, Area in SQUARE DEGREES -----
function Get-LdnShapes {
    $q = Invoke-Vizier 'SELECT LDN, Area FROM "VII/7A/ldn"'
    $iLdn  = $q.ColIdx['LDN']
    $iArea = $q.ColIdx['Area']
    $out = foreach ($line in $q.Rows) {
        $cols = $line -split "`t"
        $ldnStr  = $cols[$iLdn].Trim('"', ' ')
        $areaStr = $cols[$iArea].Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($ldnStr) -or [string]::IsNullOrWhiteSpace($areaStr)) { continue }
        # VII/7A Area is in sq-deg; convert to arcmin^2 (1 deg = 60 arcmin; 1 sq-deg = 3600 arcmin^2).
        $areaArcmin2 = [double]$areaStr * 3600.0
        $diam = 2.0 * [Math]::Sqrt($areaArcmin2 / [Math]::PI)
        [PSCustomObject][ordered]@{ Seq = [int]$ldnStr; Maj = [Math]::Round($diam, 2); Min = [Math]::Round($diam, 2); PA = 0 }
    }
    Save-Shapes 'LDN' @($out)
}

# ----- Ced (VII/231/catalog): ~420 entries, Dim1/Dim2 in arcmin. -----
# The catalog number carries an optional suffix modifier (m_Ced: 'a','b',...)
# for sub-condensations; we only emit shapes for the base entry (m_Ced blank)
# because our CatalogIndex packing does not encode the modifier. Sub-entries
# fall back to the existing fixed-circle marker.
function Get-CedShapes {
    $q = Invoke-Vizier 'SELECT Ced, m_Ced, Dim1, Dim2 FROM "VII/231/catalog"'
    $iCed  = $q.ColIdx['Ced']
    $iMod  = $q.ColIdx['m_Ced']
    $iDim1 = $q.ColIdx['Dim1']
    $iDim2 = $q.ColIdx['Dim2']
    $out = foreach ($line in $q.Rows) {
        $cols = $line -split "`t"
        $cedStr = $cols[$iCed].Trim('"', ' ')
        $modStr = $cols[$iMod].Trim('"', ' ')
        $d1Str  = $cols[$iDim1].Trim('"', ' ')
        $d2Str  = $cols[$iDim2].Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($cedStr)) { continue }
        if (-not [string]::IsNullOrWhiteSpace($modStr)) { continue }  # skip a/b sub-entries
        if ([string]::IsNullOrWhiteSpace($d1Str)) { continue }
        $d1 = [double]$d1Str
        $d2 = if ([string]::IsNullOrWhiteSpace($d2Str)) { $d1 } else { [double]$d2Str }
        [PSCustomObject][ordered]@{ Seq = [int]$cedStr; Maj = [Math]::Round($d1, 2); Min = [Math]::Round($d2, 2); PA = 0 }
    }
    Save-Shapes 'Ced' @($out)
}

Write-Host 'Fetching Dobashi...';  Get-DobashiShapes
Write-Host 'Fetching Barnard...';  Get-BarnardShapes
Write-Host 'Fetching LDN...';      Get-LdnShapes
Write-Host 'Fetching Ced...';      Get-CedShapes
