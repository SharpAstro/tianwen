
$csvFiles = @{ NGC = 'NGC'; 'NGC.addendum' = 'addendum' }

$csvFiles.GetEnumerator() | ForEach-Object {
    $csvPath = "$PSScriptRoot/../../OpenNGC/database_files/$($_.Value).csv"
    $destCsv = "$PSScriptRoot/$($_.Key).csv"
    Copy-Item $csvPath $destCsv
    $uncompressedSize = (Get-Item $destCsv).Length
    & lzip -9 $destCsv
    if (Test-Path "$destCsv.lz") {
        $compressedSize = (Get-Item "$destCsv.lz").Length
        $ratio = if ($uncompressedSize -gt 0) { $compressedSize / $uncompressedSize * 100 } else { 0 }
        Write-Host ("{0}.lz: {1:N0} -> {2:N0} bytes ({3:N1}%)" -f $_.Key, $uncompressedSize, $compressedSize, $ratio)
    }
}
