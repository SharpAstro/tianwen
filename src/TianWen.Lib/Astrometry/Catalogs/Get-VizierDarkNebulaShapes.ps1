[CmdletBinding()]
param(
    # Merge Dobashi clouds whose centers are within (rep.radius + this value) arcmin
    # of an already-claimed cluster representative. 20 arcmin collapses the
    # rho Oph / Aquila Rift / Taurus sub-cores into a handful of super-clouds.
    [double]$DobashiMergeArcmin = 20.0
)

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
# Raw Dobashi entries are tiny (median Maj ~ 5-10 arcmin) so at wide FOV
# hundreds of them cross the overlay's on-screen pixel threshold and render
# as indistinguishable speckle even over the baked dust texture. We greedy-
# cluster them here: sort by diameter descending so the largest clouds seed
# clusters, absorb any neighbour whose center lies within (rep.radius +
# DobashiMergeArcmin) of the rep, and emit one shape per cluster keyed to
# the representative's Seq. Absorbed members drop out of the shape file so
# the overlay no longer draws them individually.
function Get-DobashiShapes {
    # The Dobashi table (J/PASJ/63/S1/table8) stores positions as GLON/GLAT.
    # VizieR's ADQL parser rejects "_RA_icrs"/"_DE_icrs" when named explicitly
    # (they are VOTable post-processor meta-columns, not real ADQL fields),
    # but SELECT * brings them through. We just have to accept pulling ~14
    # columns for 7600 rows -- bandwidth is negligible.
    $q = Invoke-Vizier 'SELECT * FROM "J/PASJ/63/S1/table8"'
    $iSeq  = $q.ColIdx['Seq']
    $iArea = $q.ColIdx['Area']
    $iRa   = $q.ColIdx['_RA_icrs']
    $iDe   = $q.ColIdx['_DE_icrs']
    if ($iRa -lt 0 -or $iDe -lt 0) { throw "Expected _RA_icrs/_DE_icrs columns, got: $($q.Header -join ',')" }

    $entries = foreach ($line in $q.Rows) {
        $cols = $line -split "`t"
        $seqStr  = $cols[$iSeq].Trim('"', ' ')
        $areaStr = $cols[$iArea].Trim('"', ' ')
        $raStr   = $cols[$iRa].Trim('"', ' ')
        $deStr   = $cols[$iDe].Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($seqStr) -or [string]::IsNullOrWhiteSpace($areaStr)) { continue }
        if ([string]::IsNullOrWhiteSpace($raStr) -or [string]::IsNullOrWhiteSpace($deStr)) { continue }
        $diam = 2.0 * [Math]::Sqrt([double]$areaStr / [Math]::PI)
        [PSCustomObject]@{
            Seq  = [int]$seqStr
            Diam = $diam
            Ra   = [double]$raStr
            Dec  = [double]$deStr
        }
    }

    Write-Host ("  Loaded {0} Dobashi rows; clustering at {1:N1} arcmin..." -f $entries.Count, $DobashiMergeArcmin)

    # Sort by diameter descending so largest clouds seed clusters first.
    $sorted = $entries | Sort-Object -Property Diam -Descending
    $clusters = [System.Collections.Generic.List[object]]::new()

    foreach ($e in $sorted) {
        $merged = $false
        $cosDec = [Math]::Cos($e.Dec * [Math]::PI / 180.0)
        for ($i = 0; $i -lt $clusters.Count; $i++) {
            $c = $clusters[$i]
            # Flat approximation is fine for arcmin-scale comparisons; use
            # cos(dec) of the ENTRY (not the rep) so entries approaching from
            # the pole side don't get under-counted.
            $dRa = ($e.Ra - $c.Ra) * $cosDec * 60.0
            $dDe = ($e.Dec - $c.Dec) * 60.0
            $dist = [Math]::Sqrt($dRa * $dRa + $dDe * $dDe)
            # Merge if entry center sits inside rep's native radius plus the
            # configured buffer. Using rep.Diam/2 (not cluster.VisualRadius)
            # prevents unbounded chain-growth where absorbing one entry
            # progressively widens the next merge envelope.
            $threshold = $c.RepRadius + $DobashiMergeArcmin
            if ($dist -le $threshold) {
                $farEdge = $dist + $e.Diam / 2.0
                if ($farEdge -gt $c.VisualRadius) { $c.VisualRadius = $farEdge }
                $c.MemberCount++
                $merged = $true
                break
            }
        }
        if (-not $merged) {
            $clusters.Add([PSCustomObject]@{
                Seq          = $e.Seq
                Ra           = $e.Ra
                Dec          = $e.Dec
                RepRadius    = $e.Diam / 2.0
                VisualRadius = $e.Diam / 2.0
                MemberCount  = 1
            }) | Out-Null
        }
    }

    $mergedCount = ($clusters | Where-Object { $_.MemberCount -gt 1 }).Count
    $absorbedCount = ($clusters | Measure-Object -Property MemberCount -Sum).Sum - $clusters.Count
    Write-Host ("  Produced {0} clusters ({1} merged from {2} member absorption, solo {3})" -f $clusters.Count, $mergedCount, $absorbedCount, ($clusters.Count - $mergedCount))

    $out = foreach ($c in $clusters) {
        $diam = 2.0 * $c.VisualRadius
        [PSCustomObject][ordered]@{ Seq = $c.Seq; Maj = [Math]::Round($diam, 2); Min = [Math]::Round($diam, 2); PA = 0 }
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
