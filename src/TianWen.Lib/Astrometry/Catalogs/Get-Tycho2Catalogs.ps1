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
            $hipNumber = $maybeHip.Substring(0, 6)
            $hipCCDM = $maybeHip.Substring(6)
            if (-not [int]::TryParse($hipNumber, [cultureinfo]::InvariantCulture, [ref] $hip)) {
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

        $hd1 = if ($null -ne $InputObject.HD -and $InputObject.HD.Length -ge 1) { $InputObject.HD[0] } else { 0 }
        $hd2 = if ($null -ne $InputObject.HD -and $InputObject.HD.Length -eq 2) { $InputObject.HD[1] } else { 0 }

        # always store in little endian as it is more common
        $isLittleEndian = [BitConverter]::IsLittleEndian
        
        # first part of ID can be inferred from file name
        # $tycId1N = $tycIdShort[0]
        $tycId2 = [BitConverter]::GetBytes($tycIdShort[1])
        if (-not $isLittleEndian) {
            [array]::Reverse($tycId2)
        }
        [byte]$tycId3 = $tycIdShort[2]

        $hipBytes = [BitConverter]::GetBytes($InputObject.HIP)
        $hd1Bytes = [BitConverter]::GetBytes($hd1)
        $hd2Bytes = [BitConverter]::GetBytes($hd2)

        $raHBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits($InputObject.RA))
        $decBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits($InputObject.Dec))

        if (-not $isLittleEndian) {
            [array]::Reverse($hipBytesN)
            [array]::Reverse($hd1BytesN)
            [array]::Reverse($hd2BytesN)

            [array]::Reverse($raHBytesN)
            [array]::Reverse($decBytesN)
        }

        $entry = [byte[]]::new(14)
        # [array]::Copy($tycId1, 0, $entry, 0, 2)
        [array]::Copy($tycId2, 0, $entry, 0, 2)
        $entry[2] = $tycId3
        
        [array]::Copy($raHBytes, 0, $entry, 3, 4)
        [array]::Copy($decBytes, 0, $entry, 7, 4)
        [array]::Copy($hipBytes, 0, $entry, 5, 3)
        [array]::Copy($hd1Bytes, 0, $entry, 8, 3)
        [array]::Copy($hd2Bytes, 0, $entry, 11, 3)
        
        $stream.Write($entry)
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

        $maybeHD = $_.Substring(14, 6)
        $hd = 0
        if ([int]::TryParse($maybeHD, [cultureinfo]::InvariantCulture, [ref] $hd)) {
            
            $existing = $CatalogTable[$tycId]
            if ($null -eq $existing) {
                $CatalogTable[$tycId] = @($hd)
            } else {
                $CatalogTable[$tycId] += $hd
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
        $outputData = [PSCustomObject] @{ Streams = [System.IO.FileStream[]]::new($cat.StreamCount) }

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
                $formattedStreamId = $($i + 1).ToString('D4')

                $outputData.Streams[$i].Close()
            }
        }

        $outTar = [System.IO.Path]::Combine($location, "$($folder).bin.tar.lzma")
        Write-Host "Writing output to $($outTar)"
        if (Test-Path $outTar) {
            Remove-Item $outTar
        }

        $tmpBinOutFolder =  [System.IO.Path]::Combine($location, "out")

        & tar --lzma -c -f "$outTar" -C "$($tmpBinOutFolder)" *

        Move-Item -Force $outTar $PSScriptRoot
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