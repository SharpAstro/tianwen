
$csvFiles = @{ NGC = 'NGC'; 'NGC.addendum' = 'addendum' }

$csvFiles.GetEnumerator() | ForEach-Object {
    $null = 7z -mx9 -scsUTF-8 a "$($_.Key).csv.gz" "$PSScriptRoot/../../OpenNGC/database_files/$($_.Value).csv"
}
