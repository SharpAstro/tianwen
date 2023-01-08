[CmdLetBinding()]
param(
    [string] $Cat = $null,
    [string] $Filter = '.*'
)

$commonFilter = "^(Barnard|RCW|LDN|GUM|SH|NAME|NGC|IC|Ced|CG |M |HD|HR|VDB|HH|Dobashi|DG )"
$starCatFilter = '^(HD|HR|NAME|2MASS|[*]|M |(NGC|IC)\s+\d+[A-Za-z]?$)'

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
        HD = $starCatFilter
        DG = $commonFilter
    }
} else {
    $catalogs = [ordered]@{
        $Cat = $Filter
    }
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