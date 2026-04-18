[CmdletBinding()]
param()

# Pulls the Dobashi 2011 dark-cloud catalog from VizieR (J/PASJ/63/S1/table8) and
# writes Dobashi.shapes.json.lz next to the existing Dobashi.json.lz. The Simbad
# export only carries ids + position + VMag; this side-car adds a size estimate
# so the sky-map overlay can draw Dobashi clouds as ellipses instead of falling
# back to a fixed-size circle (which forced a binary FOV cutoff to suppress
# clutter). We store the raw Area (arcmin^2) and derive major/minor at load time
# (clouds have no published ellipse, so we treat each as circular:
# major = minor = 2 * sqrt(Area / pi), position angle = 0).

$tapUrl = 'https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync'
$adql   = 'SELECT Seq, Area FROM "J/PASJ/63/S1/table8"'
$body   = @{ REQUEST='doQuery'; LANG='ADQL'; FORMAT='tsv'; QUERY=$adql }

Write-Host "Querying VizieR TAP for $adql"
$tsv = (Invoke-WebRequest -Method Post -Uri $tapUrl -Body $body -UseBasicParsing).Content

$lines = $tsv -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }
if ($lines.Count -lt 2) { throw "TAP returned no rows" }

# First line is the header.
$header = $lines[0] -split "`t"
$iSeq   = [Array]::IndexOf($header, 'Seq')
$iArea  = [Array]::IndexOf($header, 'Area')
if ($iSeq -lt 0 -or $iArea -lt 0) { throw "Expected Seq + Area columns, got: $($header -join ',')" }

$entries = foreach ($line in $lines | Select-Object -Skip 1) {
    $cols = $line -split "`t"
    $seqStr  = $cols[$iSeq].Trim('"', ' ')
    $areaStr = $cols[$iArea].Trim('"', ' ')
    if ([string]::IsNullOrWhiteSpace($seqStr) -or [string]::IsNullOrWhiteSpace($areaStr)) { continue }
    [PSCustomObject][ordered]@{
        Seq  = [int]$seqStr
        Area = [double]$areaStr
    }
}

$entryCount = ($entries | Measure-Object).Count
Write-Host "Retrieved $entryCount Dobashi entries"

$outFile = "$PSScriptRoot/Dobashi.shapes.json"
$lzFile  = "$outFile.lz"
if (Test-Path $lzFile) { Remove-Item $lzFile }
$entries | ConvertTo-Json -Compress | Out-File -Encoding UTF8NoBOM $outFile
$uncompressedSize = [int](Get-Item $outFile).Length
$null = lzip -9 $outFile
if (Test-Path $lzFile) {
    $compressedSize = (Get-Item $lzFile).Length
    $ratio = if ($uncompressedSize -gt 0) { $compressedSize / $uncompressedSize * 100 } else { 0 }
    Write-Host ("  Dobashi.shapes.json.lz: {0:N0} -> {1:N0} bytes ({2:N1}%), {3} entries" -f $uncompressedSize, $compressedSize, $ratio, $entryCount)
}
