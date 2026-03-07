param(
    [switch] $ForceDownload,
    [switch] $ForceProcessing
)

# HIPS main
$cats = [ordered]@{
    # hip     = [PSCustomObject]@{ Cat = 'I/239';    File = 'hip_main' } 
    # hip2    = [PSCustomObject]@{ Cat = 'I/311';    File = 'hip2.dat.gz' } 
    # hd      = [PSCustomObject]@{ Cat = 'III/135A'; File = 'catalog.dat.gz' } 
    # hde     = [PSCustomObject]@{ Cat = 'III/182';  File = 'catalog.dat.gz' }
    tyc2_hd = [PSCustomObject]@{ Cat = 'J/A+A/386/709';  File = 'tyc2_hd.dat.gz'; Data = @{ } }
    tyc2    = [PSCustomObject]@{ Cat = 'I/259';  File = 'tyc2.dat.{0}.gz'; FileCount = 20; StreamCount = 9537 }
    leda    = [PSCustomObject]@{ Cat = 'VII/237'; File = 'pgc.dat.gz'; }
}

function ConvertFrom-EpochRADec
{
    param([double] $RA1, [double] $Dec1, [double] $Epoch1, [double] $Epoch2)

    $ConvH = [Math]::PI / 12.0
    $ConvD = [Math]::PI / 180.0

    $precessedRadians = ConvertFrom-EpochRadians -RA1Rad $Ra1 * $ConvH -Dec1Rad $Dec1 * $ConvD -Epoch1 $Epoch1 -Epoch2 $Epoch2

    return [PSCustomObject]@{
        RA = $precessedRadians.RA / $ConvH
        Dec = $precessedRadians.Dec / $ConvD
    }
}

function ConvertFrom-EpochRadians
{
    param([double] $RA1Rad, [double] $Dec1Rad, [double] $Epoch1, [double] $Epoch2)

    $cdr = [Math]::PI / 180.0;
    $csr = $cdr / 3600.0;
    $a = [Math]::Cos($Dec1Rad);
    $x1 = [double[]]::new(3)
    $x1[0] = $a * [Math]::Cos($RA1Rad)
    $x1[1] = $a * [Math]::Sin($RA1Rad)
    $x1[2] = [Math]::Sin($Dec1Rad)
    $t = 0.001 * ($Epoch2 - $Epoch1)
    $st = 0.001 * ($Epoch1 - 1900.0)
    $a = $csr * $t * (23042.53 + $st * (139.75 + 0.06 * $st) + $t * (30.23 - 0.27 * $st + 18.0 * $t))
    $b = $csr * $t * $t * (79.27 + 0.66 * $st + 0.32 * $t) + $a
    $c = $csr * $t * (20046.85 - $st * (85.33 + 0.37 * $st) + $t * (-42.67 - 0.37 * $st - 41.8 * $t))
    $sina = [Math]::Sin($a)
    $sinb = [Math]::Sin($b)
    $sinc = [Math]::Sin($c)
    $cosa = [Math]::Cos($a)
    $cosb = [Math]::Cos($b)
    $cosc = [Math]::Cos($c)
    $r = [double[,]]::new(3, 3)
    $r[0, 0] = $cosa * $cosb * $cosc - $sina * $sinb
    $r[0, 1] = -$cosa * $sinb - $sina * $cosb * $cosc
    $r[0, 2] = -$cosb * $sinc
    $r[1, 0] = $sina * $cosb + $cosa * $sinb * $cosc
    $r[1, 1] = $cosa * $cosb - $sina * $sinb * $cosc
    $r[1, 2] = -$sinb * $sinc
    $r[2, 0] = $cosa * $sinc
    $r[2, 1] = -$sina * $sinc
    $r[2, 2] = $cosc;
    $x2 = [double[]]::new(3)
    for ($i = 0; $i -lt 3; $i++)
    {
        $x2[$i] = $r[$i, 0] * $x1[0] + $r[$i, 1] * $x1[1] + $r[$i, 2] * $x1[2];
    }
    $ra2 = [Math]::Atan2($x2[1], $x2[0])
    if ($ra2 -lt 0.0)
    {
        $ra2 += 2.0 * [Math]::PI
    }
    $dec2 = [Math]::Asin($x2[2])

    return [PSCustomObject]@{ RA = $ra2; Dec = $dec2 }
}

function ConvertFrom-Tycho2_ASCIIDat
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $UnzippedDataFileName,
        [Parameter(Mandatory = $true)] [hashtable] $HDCrossTable
    )

    $data = Get-Content -Encoding ASCII $unzippedDataFileName

    $data | ForEach-Object {
        $values = $_.Split('|')
        
        $tycIdComp = $values[0].Split(' ')
        $tycId = "TYC $([string]::Join('-', $tycIdComp))"
        $tycIdShort = [short[]]::new(3)

        for ($i = 0; $i -lt $tycIdShort.Length; $i++) {
            [short]$s = 0
            if (-not [short]::TryParse($tycIdComp[$i], [cultureinfo]::InvariantCulture, [ref] $s)) {
                Write-Warning "$tycId : Failed to convert $($tycIdComp[$i]) to short"                
            }

            $tycIdShort[$i] = $s
        }

        $posType = $values[1]

        $maybeHip = $values[23]
        $hip = 0
        if (-not [string]::IsNullOrWhiteSpace($maybeHip)) {
            $hipNumber = $maybeHip.Substring(0, 6).Trim()
            $hipCCDM = $maybeHip.Substring(6)
            if ($hipNumber -contains ' ' -or -not [int]::TryParse($hipNumber, [cultureinfo]::InvariantCulture, [ref] $hip)) {
                Write-Warning "$tycId : Invalid HIP: $maybeHip"
            }
        } else {
            $hipCCDM = ''
        }

        $raIdx = 2
        $decIdx = 3
        if ($posType -eq 'X') {
            $raIdx = 24
            $decIdx = 25
        }

        [float]$ra = -999
        if ([float]::TryParse($values[$raIdx], [cultureinfo]::InvariantCulture, [ref] $ra)) {
            $ra /= 24.0
        } else {
            Write-Warning "$tycId : Invalid RA: $($values[$raIdx])"
        }
        
        [float]$dec = -999
        if (-not [float]::TryParse($values[$decIdx], [cultureinfo]::InvariantCulture, [ref] $dec)) {
            Write-Warning "$tycId : Invalid DEC: $($values[$raIdx])"
        }

        if ($posType -eq 'X') {
            $raDecJ2000 = ConvertFrom-EpochRADec -RA1 $ra -Dec1 $dec -Epoch1 1991.5 -Epoch2 2000.0
            $ra = $raDecJ2000.RA
            $dec = $raDecJ2000.Dec
        }

        [PSCustomObject]@{
            ID = $tycId
            IDComp = $tycIdShort
            HIP = $hip
            HD = $HDCrossTable[$tycId]
            HIPCCDM = $hipCCDM
            RA = $ra
            Dec = $dec
        }
    }
}

function ConvertTo-Tycho2_BinTable
{
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)] [PSCustomObject] $InputObject,
        [Parameter(Mandatory = $true)] [PSCustomObject] $OutputData
    )

    process {
        $tycIdShort = $InputObject.IDComp

        $gscIdx = $tycIdShort[0] - 1
        $stream = $OutputData.Streams[$gscIdx]

        # always store in little endian as it is more common
        $isLittleEndian = [BitConverter]::IsLittleEndian

        # first part of ID can be inferred from file name
        # $tycId1N = $tycIdShort[0]
        $tycId2 = [BitConverter]::GetBytes([int16]$tycIdShort[1])
        if (-not $isLittleEndian) { [array]::Reverse($tycId2) }
        [byte]$tycId3 = $tycIdShort[2]

        $raHBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits($InputObject.RA))
        $decBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits($InputObject.Dec))

        if (-not $isLittleEndian) {
            [array]::Reverse($raHBytes)
            [array]::Reverse($decBytes)
        }

        # entry: tycId2(2) + tycId3(1) + RA(4) + Dec(4) = 11 bytes
        $entry = [byte[]]::new(11)
        [array]::Copy($tycId2, 0, $entry, 0, 2)
        $entry[2] = $tycId3
        [array]::Copy($raHBytes, 0, $entry, 3, 4)
        [array]::Copy($decBytes, 0, $entry, 7, 4)
        $stream.Write($entry)

        # accumulate HIP -> TYC mapping
        if ($InputObject.HIP -ne 0) {
            $hip = $InputObject.HIP
            if (-not $OutputData.HIPMap.ContainsKey($hip)) {
                $OutputData.HIPMap[$hip] = [System.Collections.Generic.List[short[]]]::new()
            }
            [void]$OutputData.HIPMap[$hip].Add($tycIdShort)
        }

        # accumulate HD -> TYC mapping
        if ($null -ne $InputObject.HD) {
            foreach ($hd in $InputObject.HD) {
                if ($hd -ne 0) {
                    if (-not $OutputData.HDMap.ContainsKey($hd)) {
                        $OutputData.HDMap[$hd] = [System.Collections.Generic.List[short[]]]::new()
                    }
                    [void]$OutputData.HDMap[$hd].Add($tycIdShort)
                }
            }
        }
    }
}

# Writes a flat fixed-size cross-reference binary file where each entry's position determines the key:
# entry at offset (key-1)*5 holds the matching TYC star as GSC_int16(2) + Num_int16(2) + Comp_byte(1).
# Empty slots (no match) are all zeros. Keys are NOT stored in the file.
# When multiple TYC stars share the same key, those are written to a JSON sidecar file instead.
function Write-CrossRefFiles
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [hashtable] $Map,
        [Parameter(Mandatory = $true)] [string] $BinFile,
        [Parameter(Mandatory = $true)] [string] $JsonFile
    )

    if ($Map.Count -eq 0) { return }

    $isLittleEndian = [BitConverter]::IsLittleEndian
    $collisions = [ordered]@{}

    $sortedMap = $Map.GetEnumerator() | Sort-Object Key
    $maxKey    = $sortedMap | Select-Object -Last 1 | ForEach-Object { $_.Key }
    $RecordSize = 5
    $buffer    = [byte[]]::new($maxKey * $RecordSize)  # all zeros = empty slots

    foreach ($kvp in $sortedMap) {
        $key     = $kvp.Key
        $tycList = $kvp.Value

        if ($tycList.Count -eq 1) {
            $tycId  = $tycList[0]
            $offset = ($key - 1) * $RecordSize

            $gscBytes = [BitConverter]::GetBytes([int16]$tycId[0])
            $numBytes = [BitConverter]::GetBytes([int16]$tycId[1])
            if (-not $isLittleEndian) {
                [array]::Reverse($gscBytes)
                [array]::Reverse($numBytes)
            }
            [array]::Copy($gscBytes, 0, $buffer, $offset,     2)
            [array]::Copy($numBytes, 0, $buffer, $offset + 2, 2)
            $buffer[$offset + 4] = [byte]$tycId[2]
        } else {
            $collisions["$key"] = @($($tycList) | ForEach-Object { "$($_[0])-$($_[1])-$($_[2])" })
        }
    }

    [System.IO.File]::WriteAllBytes($BinFile, $buffer)
    & 7z a -txz "$BinFile.xz" $BinFile
    Remove-Item $BinFile

    if ($collisions.Count -gt 0) {
        $collisions | ConvertTo-Json | Set-Content -Encoding UTF8 $JsonFile
        & 7z a -txz "$JsonFile.xz" $JsonFile
        Write-Host "  $($collisions.Count) collision(s) written to $JsonFile.xz"
    }
}

function ConvertFrom-Tycho2_HD_ASCIIDat
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $UnzippedDataFileName,
        [Parameter(Mandatory = $true)] [hashtable] $CatalogTable
    )

    $data = Get-Content -Encoding ASCII $unzippedDataFileName

    $data | ForEach-Object {

        $tycIdComp = $_.Substring(0, 12).Split(' ')
        $tycId = "TYC $([string]::Join('-', $tycIdComp))"

        $maybeHD = $_.Substring(14, 7)
        $hd = 0
        if ([int]::TryParse($maybeHD, [cultureinfo]::InvariantCulture, [ref] $hd)) {
            $hdStr = "HD $hd"
            $existingTycho2hd = $CatalogTable[$tycId]
            if ($null -eq $existingTycho2hd) {
                $CatalogTable[$tycId] = @($hd)
            } else {
                $CatalogTable[$tycId] += $hd
            }

            $existingHD2Tycho = $CatalogTable[$hdStr]
            if ($null -eq $existingHD2Tycho) {
                $CatalogTable[$hdStr] = @($tycId)
            } else {
                $CatalogTable[$hdStr] += $tycId
            }
        } else {
            Write-Warning "$tycId : Invalid HD: $maybeHD"
        }
    }
}

Push-Location $PSScriptRoot

Push-Location 'tmp-data'

$cats.GetEnumerator() | ForEach-Object {
    $folder = $_.Key
    $cat = $_.Value

    if (Test-Path -PathType Container -Path $folder) {
        $needsDownload = $ForceDownload
    } else {
        New-Item -ItemType Directory $folder
        $needsDownload = $true
    }
    
    Push-Location $folder
    Write-Host "Folder: $folder Cat: $($cat.Cat) $($cat.File)"

    $dataFileCount = $cat.FileCount
    $isSplitFile = $null -ne $dataFileCount -and $dataFileCount -ge 1
    
    $decDigits = if ($isSplitFile) { [int]($dataFileCount / 10) } else { -1 }
    $digitFormat = if ($isSplitFile) { "{0:$([string]::new('0', $decDigits))}" } else { $null }

    if ($needsDownload) {
        & curl -LO https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/ReadMe

        Move-Item -Force ReadMe "$($folder).readme.txt"

        if ($isSplitFile) {
            for ($i = 0; $i -lt $dataFileCount; $i++) {
                $dataFileName = [string]::Format($cat.File, [string]::Format($digitFormat, $i))

                Write-Host "Downloading: https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/$dataFileName"

                & curl -LO https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/$dataFileName    
            } 
        } else {
            & curl -LO https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/$($cat.File)
        }
    } else {
        Write-Host 'Skipping download as folder is present. Use -ForceDownload to download anyway.'
    }

    $catalogTable = $cat.Data

    if ($isSplitFile) {
        $unzippedFilePattern = [System.IO.Path]::GetFileNameWithoutExtension($cat.File)
        $location = Get-Location
        $outputData = [PSCustomObject] @{
            Streams = [System.IO.FileStream[]]::new($cat.StreamCount)
            HIPMap  = @{}
            HDMap   = @{}
        }

        $needsProcessing = $false
        for ($i = 0; $i -lt $cat.StreamCount; $i++) {
            $formattedStreamId = $($i + 1).ToString('D4')
            $tmpBinFolder = [System.IO.Path]::Combine($location, 'out', $formattedStreamId[0])
            if (-not (Test-Path -PathType Container $tmpBinFolder)) {
                $null = New-Item -ItemType Directory $tmpBinFolder
            }
            $tmpBinFile = [System.IO.Path]::Combine($tmpBinFolder, "$($folder)_$($($i + 1).ToString('D4')).bin")
            
            if (-not (Test-Path $tmpBinFile) -or $ForceProcessing) {
                $needsProcessing = $true
                $outputData.Streams[$i] = [System.IO.File]::Open($tmpBinFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            }
        }
    
        for ($i = 0; $i -lt $dataFileCount; $i++) {
            $formattedDigit = [string]::Format($digitFormat, $i)
            $zippedDataFileName = [string]::Format($cat.File, $formattedDigit)
            $unzippedDataFileName = [string]::Format($unzippedFilePattern, $formattedDigit)

            if (-not (Test-Path $unzippedDataFileName) -or $ForceDownload) {
                Write-Host "Extracting $($zippedDataFileName)"
                if (Test-Path $unzippedDataFileName) {
                    Remove-Item $unzippedDataFileName
                }
                & 7z e -y $zippedDataFileName
            } else {
                Write-Host "Skip extracting $($zippedDataFileName) as $($unzippedDataFileName) already exists"
            }

            if ($folder -eq 'tyc2' -and $needsProcessing) {
                ConvertFrom-Tycho2_ASCIIDat -UnzippedDataFileName $unzippedDataFileName -HDCrossTable $cats['tyc2_hd'].Data
                    | ConvertTo-Tycho2_BinTable -OutputData $outputData
            }
        }

        if ($needsProcessing) {
            for ($i = 0; $i -lt $cat.StreamCount; $i++) {
                $outputData.Streams[$i].Close()
            }
        }

        if ($folder -eq 'tyc2' -and $needsProcessing) {
            Write-Host "Writing HIP cross-reference..."
            Write-CrossRefFiles `
                -Map      $outputData.HIPMap `
                -BinFile  ([System.IO.Path]::Combine($PSScriptRoot, 'hip_to_tyc.bin')) `
                -JsonFile ([System.IO.Path]::Combine($PSScriptRoot, 'hip_to_tyc_multi.json'))

            Write-Host "Writing HD cross-reference..."
            Write-CrossRefFiles `
                -Map      $outputData.HDMap `
                -BinFile  ([System.IO.Path]::Combine($PSScriptRoot, 'hd_to_tyc.bin')) `
                -JsonFile ([System.IO.Path]::Combine($PSScriptRoot, 'hd_to_tyc_multi.json'))
        }

        # Simple binary archive: int32 streamCount, then streamCount × int32 byte-offsets, then concatenated stream data.
        # Stream N's offset entry is at byte position N*4 (1-indexed), i.e. file 1 offset is at byte 4.
        $outBin = [System.IO.Path]::Combine($PSScriptRoot, "$($folder).bin")
        Write-Host "Writing binary catalog to $($outBin)"
        if (Test-Path $outBin) {
            Remove-Item $outBin
        }

        $streamCount = $cat.StreamCount
        $headerSize = ($streamCount + 1) * 4
        $isLittleEndian = [BitConverter]::IsLittleEndian

        # Collect stream file paths and sizes to precompute offsets
        $streamFiles = [string[]]::new($streamCount)
        $streamSizes = [int[]]::new($streamCount)
        for ($i = 0; $i -lt $streamCount; $i++) {
            $formattedStreamId = $($i + 1).ToString('D4')
            $tmpBinFolder = [System.IO.Path]::Combine($location, 'out', $formattedStreamId[0])
            $streamFiles[$i] = [System.IO.Path]::Combine($tmpBinFolder, "$($folder)_$($formattedStreamId).bin")
            if (Test-Path $streamFiles[$i]) {
                $streamSizes[$i] = [int](Get-Item $streamFiles[$i]).Length
            }
        }

        $outStream = [System.IO.File]::Open($outBin, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        try {
            # Write header: stream count + precomputed offsets
            $countBytes = [BitConverter]::GetBytes([int32]$streamCount)
            if (-not $isLittleEndian) { [array]::Reverse($countBytes) }
            $outStream.Write($countBytes, 0, 4)

            $offset = $headerSize
            for ($i = 0; $i -lt $streamCount; $i++) {
                $offsetBytes = [BitConverter]::GetBytes([int32]$offset)
                if (-not $isLittleEndian) { [array]::Reverse($offsetBytes) }
                $outStream.Write($offsetBytes, 0, 4)
                $offset += $streamSizes[$i]
            }

            # Write concatenated stream data
            for ($i = 0; $i -lt $streamCount; $i++) {
                if ($streamSizes[$i] -gt 0) {
                    $streamData = [System.IO.File]::ReadAllBytes($streamFiles[$i])
                    $outStream.Write($streamData, 0, $streamData.Length)
                }
            }
        }
        finally {
            $outStream.Close()
        }

        & 7z a -txz "$outBin.xz" $outBin
        # Remove-Item $outBin
    } elseif ($null -ne $catalogTable) {
        $unzippedDataFileName = [System.IO.Path]::GetFileNameWithoutExtension($cat.File)

        if (-not (Test-Path $unzippedDataFileName) -or $ForceDownload) {
            Write-Host "Extracting $($cat.File)"
            if (Test-Path $unzippedDataFileName) {
                Remove-Item $unzippedDataFileName
            }
            & 7z e -y $cat.File
        } else {
            Write-Host "Skip extracting $($cat.File) as $($unzippedDataFileName) already exists"
        }

        if ($folder -eq 'tyc2_hd') {
            ConvertFrom-Tycho2_HD_ASCIIDat -UnzippedDataFileName $unzippedDataFileName -CatalogTable $catalogTable
        }
    }

    Pop-Location # folder
}

Pop-Location # tmp-data

Pop-Location # PSScriptRoot