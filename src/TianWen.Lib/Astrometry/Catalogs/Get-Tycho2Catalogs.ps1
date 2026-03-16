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

function Compress-WithLzip
{
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    $lzFile = "$Path.lz"
    if ($ForceProcessing -and (Test-Path $lzFile)) {
        Remove-Item $lzFile
    }

    $uncompressedSize = [int](Get-Item $Path).Length
    & lzip -9 -b 4MiB $Path
    if (Test-Path $lzFile) {
        $compressedSize = (Get-Item $lzFile).Length
        $ratio = if ($uncompressedSize -gt 0) { $compressedSize / $uncompressedSize * 100 } else { 0 }
        Write-Host ("  {0}: {1:N0} -> {2:N0} bytes ({3:N1}%)" -f (Split-Path $lzFile -Leaf), $uncompressedSize, $compressedSize, $ratio)
    }
}

function ConvertFrom-TycIdComponents
{
    param([string[]] $Components)
    $inv = [cultureinfo]::InvariantCulture
    [short]$t1 = 0; [short]$t2 = 0; [short]$t3 = 0
    [void][short]::TryParse($Components[0], $inv, [ref] $t1)
    [void][short]::TryParse($Components[1], $inv, [ref] $t2)
    [void][short]::TryParse($Components[2], $inv, [ref] $t3)
    return [PSCustomObject]@{ Tyc1 = $t1; Tyc2 = $t2; Tyc3 = $t3; Key = "TYC $($t1)-$($t2)-$($t3)" }
}

function ConvertFrom-EpochRADec
{
    param([double] $RA1, [double] $Dec1, [double] $Epoch1, [double] $Epoch2)

    $ConvH = [Math]::PI / 12.0
    $ConvD = [Math]::PI / 180.0

    $precessedRadians = ConvertFrom-EpochRadians -RA1Rad ($Ra1 * $ConvH) -Dec1Rad ($Dec1 * $ConvD) -Epoch1 $Epoch1 -Epoch2 $Epoch2

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

function ConvertAndWrite-Tycho2Data
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $UnzippedDataFileName,
        [Parameter(Mandatory = $true)] [hashtable] $HDCrossTable,
        [Parameter(Mandatory = $true)] [PSCustomObject] $OutputData
    )

    $resolvedPath = (Resolve-Path $UnzippedDataFileName).Path
    $lines = [System.IO.File]::ReadAllLines($resolvedPath, [System.Text.Encoding]::ASCII)
    $isLittleEndian = [BitConverter]::IsLittleEndian
    $entry = [byte[]]::new(13)
    $inv = [cultureinfo]::InvariantCulture

    foreach ($line in $lines) {
        $values = $line.Split('|')

        $tyc = ConvertFrom-TycIdComponents $values[0].Split(' ')
        $tyc1 = $tyc.Tyc1; $tyc2 = $tyc.Tyc2; $tyc3 = $tyc.Tyc3
        $tycIdShort = [short[]]@($tyc1, $tyc2, $tyc3)

        $posType = $values[1]

        $hip = 0
        $maybeHip = $values[23]
        if ($maybeHip.Length -ge 6) {
            [void][int]::TryParse($maybeHip.Substring(0, 6).Trim(), $inv, [ref] $hip)
        }

        $raIdx = 2; $decIdx = 3
        if ($posType -eq 'X') { $raIdx = 24; $decIdx = 25 }

        [float]$ra = -999; [float]$dec = -999
        if ([float]::TryParse($values[$raIdx], $inv, [ref] $ra)) {
            $ra /= 15.0
        }
        [void][float]::TryParse($values[$decIdx], $inv, [ref] $dec)

        # Note: for posType 'X' entries, fields 24/25 are the observed Tycho-2 position
        # already in ICRS (J2000 reference frame). No precession is needed — ICRS coordinates
        # only differ by proper motion between epochs, and type 'X' entries have no proper motion.
        # Applying classical precession here would incorrectly rotate the coordinate frame,
        # producing errors of ~400" for Pleiades-era stars (e.g. Electra/TYC 1799-1441-1).

        # Parse VTmag (field 19) and BTmag (field 17), 0-indexed pipe-delimited
        # Encode as biased decimag: byte = clamp(round(mag * 10) + 20, 0, 254), 0xFF = missing
        [float]$vtMag = [float]::NaN
        [float]$btMag = [float]::NaN
        [void][float]::TryParse($values[19].Trim(), $inv, [ref] $vtMag)
        [void][float]::TryParse($values[17].Trim(), $inv, [ref] $btMag)

        $vtDecimag = if ([float]::IsNaN($vtMag)) { 255 } else { [Math]::Clamp([int][Math]::Round($vtMag * 10) + 20, 0, 254) }
        $btDecimag = if ([float]::IsNaN($btMag)) { 255 } else { [Math]::Clamp([int][Math]::Round($btMag * 10) + 20, 0, 254) }

        # Write binary entry directly
        $gscIdx = $tyc1 - 1
        $stream = $OutputData.Streams[$gscIdx]

        $id2Bytes = [BitConverter]::GetBytes([int16]$tyc2)
        if (-not $isLittleEndian) { [array]::Reverse($id2Bytes) }

        $raHBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits([float]$ra))
        $decBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits([float]$dec))
        if (-not $isLittleEndian) {
            [array]::Reverse($raHBytes)
            [array]::Reverse($decBytes)
        }

        $entry[0] = $id2Bytes[0]; $entry[1] = $id2Bytes[1]
        $entry[2] = [byte]$tyc3
        $entry[3] = $raHBytes[0]; $entry[4] = $raHBytes[1]; $entry[5] = $raHBytes[2]; $entry[6] = $raHBytes[3]
        $entry[7] = $decBytes[0]; $entry[8] = $decBytes[1]; $entry[9] = $decBytes[2]; $entry[10] = $decBytes[3]
        $entry[11] = [byte]$vtDecimag
        $entry[12] = [byte]$btDecimag
        $stream.Write($entry, 0, 13)

        # track GSC region bounding box
        if ($ra -lt $OutputData.GscMinRA[$gscIdx])  { $OutputData.GscMinRA[$gscIdx]  = $ra }
        if ($ra -gt $OutputData.GscMaxRA[$gscIdx])  { $OutputData.GscMaxRA[$gscIdx]  = $ra }
        if ($dec -lt $OutputData.GscMinDec[$gscIdx]) { $OutputData.GscMinDec[$gscIdx] = $dec }
        if ($dec -gt $OutputData.GscMaxDec[$gscIdx]) { $OutputData.GscMaxDec[$gscIdx] = $dec }

        # accumulate HIP -> TYC mapping
        if ($hip -ne 0) {
            if (-not $OutputData.HIPMap.ContainsKey($hip)) {
                $OutputData.HIPMap[$hip] = [System.Collections.Generic.List[short[]]]::new()
            }
            [void]$OutputData.HIPMap[$hip].Add($tycIdShort)
        }

        # accumulate HD -> TYC mapping
        $hdList = $HDCrossTable[$tyc.Key]
        if ($null -ne $hdList) {
            foreach ($hd in $hdList) {
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
    Compress-WithLzip $BinFile

    if ($collisions.Count -gt 0) {
        $collisions | ConvertTo-Json | Set-Content -Encoding UTF8 $JsonFile
        Compress-WithLzip $JsonFile
        Write-Host "  $($collisions.Count) collision(s) written to $JsonFile.jz"
    }
}

function ConvertFrom-Tycho2_HD_ASCIIDat
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $UnzippedDataFileName,
        [Parameter(Mandatory = $true)] [hashtable] $CatalogTable
    )

    $resolvedPath = (Resolve-Path $UnzippedDataFileName).Path
    $lines = [System.IO.File]::ReadAllLines($resolvedPath, [System.Text.Encoding]::ASCII)
    $inv = [cultureinfo]::InvariantCulture

    foreach ($line in $lines) {
        $tycId = (ConvertFrom-TycIdComponents $line.Substring(0, 12).Split(' ')).Key

        $maybeHD = $line.Substring(14, 7)
        $hd = 0
        if ([int]::TryParse($maybeHD, $inv, [ref] $hd)) {
            $existingTycho2hd = $CatalogTable[$tycId]
            if ($null -eq $existingTycho2hd) {
                $CatalogTable[$tycId] = @($hd)
            } else {
                $CatalogTable[$tycId] += $hd
            }

            $hdStr = "HD $hd"
            $existingHD2Tycho = $CatalogTable[$hdStr]
            if ($null -eq $existingHD2Tycho) {
                $CatalogTable[$hdStr] = @($tycId)
            } else {
                $CatalogTable[$hdStr] += $tycId
            }
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
        # Per-GSC-region bounding boxes: [gscIdx] -> (minRA, maxRA, minDec, maxDec)
        $gscMinRA  = [float[]]::new($cat.StreamCount)
        $gscMaxRA  = [float[]]::new($cat.StreamCount)
        $gscMinDec = [float[]]::new($cat.StreamCount)
        $gscMaxDec = [float[]]::new($cat.StreamCount)
        for ($j = 0; $j -lt $cat.StreamCount; $j++) {
            $gscMinRA[$j]  = [float]::MaxValue
            $gscMaxRA[$j]  = [float]::MinValue
            $gscMinDec[$j] = [float]::MaxValue
            $gscMaxDec[$j] = [float]::MinValue
        }

        $outputData = [PSCustomObject] @{
            Streams   = [System.IO.FileStream[]]::new($cat.StreamCount)
            HIPMap    = @{}
            HDMap     = @{}
            GscMinRA  = $gscMinRA
            GscMaxRA  = $gscMaxRA
            GscMinDec = $gscMinDec
            GscMaxDec = $gscMaxDec
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
                ConvertAndWrite-Tycho2Data -UnzippedDataFileName $unzippedDataFileName -HDCrossTable $cats['tyc2_hd'].Data -OutputData $outputData
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

            # Write GSC region bounding boxes: 9537 × (minRA, maxRA, minDec, maxDec) as float32
            $boundsFile = [System.IO.Path]::Combine($PSScriptRoot, 'tyc2_gsc_bounds.bin')
            Write-Host "Writing GSC bounds to $boundsFile ($($cat.StreamCount) regions)"
            $boundsIsLE = [BitConverter]::IsLittleEndian
            $boundsBuffer = [byte[]]::new($cat.StreamCount * 16)
            for ($j = 0; $j -lt $cat.StreamCount; $j++) {
                $off = $j * 16
                $b1 = [BitConverter]::GetBytes([float]$outputData.GscMinRA[$j])
                $b2 = [BitConverter]::GetBytes([float]$outputData.GscMaxRA[$j])
                $b3 = [BitConverter]::GetBytes([float]$outputData.GscMinDec[$j])
                $b4 = [BitConverter]::GetBytes([float]$outputData.GscMaxDec[$j])
                if (-not $boundsIsLE) {
                    [array]::Reverse($b1); [array]::Reverse($b2)
                    [array]::Reverse($b3); [array]::Reverse($b4)
                }
                [array]::Copy($b1, 0, $boundsBuffer, $off,      4)
                [array]::Copy($b2, 0, $boundsBuffer, $off + 4,  4)
                [array]::Copy($b3, 0, $boundsBuffer, $off + 8,  4)
                [array]::Copy($b4, 0, $boundsBuffer, $off + 12, 4)
            }
            [System.IO.File]::WriteAllBytes($boundsFile, $boundsBuffer)
            Compress-WithLzip $boundsFile
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

        Compress-WithLzip $outBin
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