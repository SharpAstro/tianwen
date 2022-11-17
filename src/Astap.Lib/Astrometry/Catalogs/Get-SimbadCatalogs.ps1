

$catalogs = @('Sh', 'RCW', 'GUM', 'LDN', 'Ced', 'Barnard')

$catalogs | ForEach-Object {
    $cat = $_
    [xml]$table = Invoke-RestMethod "http://simbad.u-strasbg.fr/simbad/sim-id?output.format=votable&Ident=$($cat)&NbIdent=cat&output.params=main_id,ids,otype,ra(d;ICRS),dec(d,ICRS)"

    $entries = $table.VOTABLE.RESOURCE.TABLE.DATA.TABLEDATA.TR | ForEach-Object {
        [PSCustomObject]@{
            MainId = $_.TD[0]
            Ids = $_.TD[1].Split('|')
            ObjType = $_.TD[2]
            Ra = [double]$_.TD[3]
            Dec = [double]$_.TD[4]
        }
    }

    $outFile = "$PSScriptRoot/$($cat).json"
    $entries | ConvertTo-Json | Out-File -Encoding UTF8NoBOM $outFile
    $null = 7z -mx9 -scsUTF-8 a "$($outFile).gz" $outFile
}