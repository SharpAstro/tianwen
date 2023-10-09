$hip2000_js = (Get-Content -Raw "$PSScriptRoot/../../hip2000/hipparcos_full_concise.js").Trim()
$hip2000_json_tmp = $hip2000_js.Replace('hipparcos_catalog=', '{ "hipparcos_catalog": ')
$hip2000_json = $hip2000_json_tmp.Substring(0, $hip2000_json_tmp.Length - 1) + '}'

$hip2000 =  $hip2000_json | ConvertFrom-Json

$tempName = "$([guid]::NewGuid().ToString('D')).bin.tmp"
$hip200BinStream = [System.IO.File]::OpenWrite("$PSScriptRoot/$tempName")

$table = @{ }
$hipFirst = [int]::MaxValue
$hipLast = 0
$entrySize = 2 * 8 + 4
try {

    foreach ($entry in $hip2000.hipparcos_catalog) {
        [int]$id = $entry[0]
        [float]$vmag = $entry[1]
        [double]$ra = $entry[2] / 15.0
        [double]$dec = $entry[3]

        $vmagB = [BitConverter]::GetBytes([ipaddress]::HostToNetworkOrder([System.BitConverter]::SingleToInt32Bits($vmag)))
        $raB = [BitConverter]::GetBytes([ipaddress]::HostToNetworkOrder([System.BitConverter]::DoubleToInt64Bits($ra)))
        $decB = [BitConverter]::GetBytes([ipaddress]::HostToNetworkOrder([System.BitConverter]::DoubleToInt64Bits($dec)))

        $bytes = [byte[]]::new($entrySize)
        [array]::Copy($raB, 0, $bytes, 0, 8)
        [array]::Copy($decB, 0, $bytes, 8, 8)
        [array]::Copy($vmagB, 0, $bytes, 16, 4)

        if ($id -lt $hipFirst) { $hipFirst = $id }
        if ($id -gt $hipLast) { $hipLast = $id }
        $table.Add($id, $bytes)
    }
    
    Write-Host "Items: $($table.Count) first HIP: $hipFirst  last HIP: $hipLast contains last: $($table.ContainsKey($hipLast))"

    $entriesWritten = 0
    $missing = 0
    for ($hip = $hipFirst; $hip -le $hipLast; $hip++) {
        if ($table.ContainsKey($hip)) {
            $entry = $table[$hip]
            $hip200BinStream.Write($entry)
        } else {
            $missing++
            $bytes = [byte[]]::new($entrySize)
            $hip200BinStream.Write($bytes)
        }
        $entriesWritten++
    }

    Write-Host "Wrote total of $entriesWritten with size $entrySize, with $missing missing entries"

    $hip200BinStream.Flush()
} finally {
    $hip200BinStream.Close()
}

Move-Item -Force $tempName "$PSScriptRoot/HIP.bin"

7z -mx9 -scsUTF-8 a HIP.bin.gz "$PSScriptRoot/HIP.bin"

Remove-Item "$PSScriptRoot/HIP.bin"
