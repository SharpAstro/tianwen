
$commonFilter = "^(Barnard|RCW|LDN|GUM|SH|NAME|NGC|IC|Ced|M |HD|HR)"

$catalogs = [ordered]@{
    Bayer = "^(HD|HR|NAME|2MASS|[*])"
    Barnard = $commonFilter
    RCW = $commonFilter
    GUM = $commonFilter
    LDN = $commonFilter
    Ced = $commonFilter
    Sh = $commonFilter
}
$outParams = "main_id,ids,otype(3),ra(d;ICRS),dec(d,ICRS),fluxdata(V)"
$catalogs.GetEnumerator() | ForEach-Object {
    $cat = $_.Name
    $filter = $_.Value
    Write-Host "Querying catalog $($cat) using ID filter $($filter)"
    [xml]$table = Invoke-RestMethod "http://simbad.u-strasbg.fr/simbad/sim-id?output.format=votable&Ident=$($cat)&NbIdent=cat&output.params=$($outParams)"

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

    $outFile = "$PSScriptRoot/$($cat).json"
    $entries | ConvertTo-Json | Out-File -Encoding UTF8NoBOM $outFile
    $null = 7z -mx9 -scsUTF-8 a "$($outFile).gz" $outFile
}