[CmdLetBinding()]
param(
    [string] $Cat = $null,
    [switch] $IsStarCat = $false,
    [switch] $SkipStarCats = $false
)

$commonFilter = "^(Barnard|RCW|LDN|GUM|SH|NAME|NGC|IC|Ced|CG\s+|M\s+|HD|HR|HIP|VDB|HH|Dobashi|DG\s+|Cl\s+\w+)"
$starCatFilter = '^(HD|HR|HIP|NAME|2MASS|[*]|M\s+|(NGC|IC)\s+\d+[A-Za-z]?$)'

# missing:
if ([string]::IsNullOrWhiteSpace($Cat)) {
    $catalogs = [ordered]@{
        HR = $starCatFilter
        Barnard = $commonFilter
        RCW = $commonFilter
        GUM = $commonFilter
        Dobashi = $commonFilter
        LDN = $commonFilter
        Ced = $commonFilter
        Sh = $commonFilter
        CG = $commonFilter
        vdB = $commonFilter # van den Bergh
        HH = $commonFilter # Herbig-Haro
        # HD = $starCatFilter
        DG = $commonFilter
        # OCl = $commonFilter
        Cl = $commonFilter
    }
} else {
    $catalogs = [ordered]@{
        $Cat = if ($sStarCat) { $starCatFilter } else { $commonFilter } 
    }
}

$outParams = "main_id,ids,otype(3),ra(d;ICRS),dec(d,ICRS),fluxdata(V)"
$catalogs.GetEnumerator() | ForEach-Object {
    $cat = $_.Name
    $filter = $_.Value
    if ($filter -eq $starCatFilter -and $SkipStarCats) {
        Write-Host "Skipping star catalog $cat"
    } else {
        
        $commonOutputQueryPart = "&output.format=votable&output.params=$($outParams)"
        
        if ($cat -eq 'Cl') { 
            $url = "https://simbad.cds.unistra.fr/simbad/sim-sam?Criteria=cat+%3D+%27Cl%27+%26+otype+%3D+%27OpenCluster%27&submit=submit+query&OutputMode=LIST&maxObject=10000$($commonOutputQueryPart)"
        } else { 
            Write-Host "Querying catalog $($cat) using ID filter $($filter)"
            $url = "http://simbad.u-strasbg.fr/simbad/sim-id?Ident=$($cat)&NbIdent=cat$($commonOutputQueryPart)"
        }

        [xml]$table = Invoke-RestMethod $url

        $entries = $table.VOTABLE.RESOURCE.TABLE.DATA.TABLEDATA.TR | ForEach-Object {
            if (-not [string]::IsNullOrWhiteSpace($_.TD[3]) -and -not [string]::IsNullOrWhiteSpace($_.TD[4])) {
                $ids = $_.TD[1].Split('|') -match $filter
                [PSCustomObject]@{
                    MainId = $_.TD[0]
                    Ids = $ids
                    ObjType = $_.TD[2]
                    Ra = [double]$_.TD[3]
                    Dec = [double]$_.TD[4]
                    VMag = if (-not [string]::IsNullOrWhiteSpace($_.TD[6])) { [double]$_.TD[6] } else { $null }
                }
            }
        }

        $entryCount = ($entries | Measure-Object).Count
        $outFile = "$PSScriptRoot/$($cat.Replace('*', '_')).json"
        $lzFile = "$outFile.lz"
        if (Test-Path $lzFile) { Remove-Item $lzFile }
        $sizeFile = "$lzFile.size"
        if (Test-Path $sizeFile) { Remove-Item $sizeFile }
        $entries | ConvertTo-Json | Out-File -Encoding UTF8NoBOM $outFile
        $uncompressedSize = [int](Get-Item $outFile).Length
        $null = lzip -9 $outFile
        if (Test-Path $lzFile) {
            $compressedSize = (Get-Item $lzFile).Length
            $ratio = if ($uncompressedSize -gt 0) { $compressedSize / $uncompressedSize * 100 } else { 0 }
            Write-Host ("  {0}: {1:N0} -> {2:N0} bytes ({3:N1}%), {4} entries" -f (Split-Path $lzFile -Leaf), $uncompressedSize, $compressedSize, $ratio, $entryCount)

            $isLE = [BitConverter]::IsLittleEndian
            $sizeBytes = [BitConverter]::GetBytes([int32]$uncompressedSize)
            $countBytes = [BitConverter]::GetBytes([int32]$entryCount)
            if (-not $isLE) { [array]::Reverse($sizeBytes); [array]::Reverse($countBytes) }
            $sizeFile = "$lzFile.size"
            $sidecar = [byte[]]::new(8)
            [array]::Copy($sizeBytes, 0, $sidecar, 0, 4)
            [array]::Copy($countBytes, 0, $sidecar, 4, 4)
            [System.IO.File]::WriteAllBytes($sizeFile, $sidecar)
        }
    }
}