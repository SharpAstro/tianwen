
$csvFiles = @{ NGC = 'NGC'; 'NGC.addendum' = 'addendum/addendum' }

$csvFiles.GetEnumerator() | ForEach-Object {
    $null = 7z -mx9 -scsUTF-8 a "$($_.Key).csv.gz" "$PSScriptRoot/../../OpenNGC/$($_.Value).csv"
}
