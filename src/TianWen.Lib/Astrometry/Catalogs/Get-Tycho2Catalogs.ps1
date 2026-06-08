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

# Writes one Tycho-2 star as a 17-byte binary entry into its GSC-region stream,
# plus pm-sidecar overflow, GSC bounding-box, and HIP cross-ref accumulation.
# Shared by the main-catalog parser (tyc2.dat) AND the Supplement-1 parser
# (suppl_1.dat) so the on-disk layout is byte-identical for both sources.
#
# 17-byte entry layout: tyc2 u16 | tyc3 u8 | RA f32 (hours) | Dec f32 (deg)
#                     | VT decimag u8 | BT decimag u8
#                     | pmRA int16 (= source mas/yr * 10)
#                     | pmDec int16 (= source mas/yr * 10).
# Inline pm encoding: signed int16, scale 0.1 mas/yr per unit. Special values:
#   0           = "no useful pm" (missing-pm entries AND legitimate zero-pm
#                  stars; both produce no drift downstream regardless of dt).
#   +/-32767    = saturation rail; consult tyc2_pm_sidecar.bin.lz with the
#                  (tyc1, tyc2, tyc3) key to recover exact int32 pm.
# Biased decimag: byte = clamp(round(mag * 10) + 20, 0, 254), 0xFF = missing.
# Caller passes RA in HOURS, Dec in DEGREES, both already at epoch J2000/ICRS
# (the supplement parser propagates its J1991.25 positions forward by proper
# motion before calling). VtMag/BtMag/PmRa/PmDec are NaN when the source is blank.
function Write-Tycho2BinaryEntry
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [int]   $Tyc1,
        [Parameter(Mandatory = $true)] [int]   $Tyc2,
        [Parameter(Mandatory = $true)] [int]   $Tyc3,
        [Parameter(Mandatory = $true)] [float] $RaHours,
        [Parameter(Mandatory = $true)] [float] $DecDeg,
        [float] $VtMag = [float]::NaN,
        [float] $BtMag = [float]::NaN,
        [float] $PmRa  = [float]::NaN,
        [float] $PmDec = [float]::NaN,
        [int]   $Hip   = 0,
        [Parameter(Mandatory = $true)] [PSCustomObject] $OutputData
    )

    $gscIdx = $Tyc1 - 1
    $stream = $OutputData.Streams[$gscIdx]
    if ($null -eq $stream) { return }   # region not opened for processing this run

    $isLittleEndian = [BitConverter]::IsLittleEndian

    $vtDecimag = if ([float]::IsNaN($VtMag)) { 255 } else { [Math]::Clamp([int][Math]::Round($VtMag * 10) + 20, 0, 254) }
    $btDecimag = if ([float]::IsNaN($BtMag)) { 255 } else { [Math]::Clamp([int][Math]::Round($BtMag * 10) + 20, 0, 254) }

    $id2Bytes = [BitConverter]::GetBytes([int16]$Tyc2)
    if (-not $isLittleEndian) { [array]::Reverse($id2Bytes) }

    $raHBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits([float]$RaHours))
    $decBytes = [BitConverter]::GetBytes([BitConverter]::SingleToInt32Bits([float]$DecDeg))
    if (-not $isLittleEndian) {
        [array]::Reverse($raHBytes)
        [array]::Reverse($decBytes)
    }

    # int16 = round(source mas/yr * 10). Saturation rails at +/-32767 trigger a
    # sidecar entry (int32 pm to preserve full source precision past 3276.7 mas/yr).
    [int]$pmRaTenths  = 0
    [int]$pmDecTenths = 0
    $pmRaSat  = $false
    $pmDecSat = $false
    if (-not [float]::IsNaN($PmRa)) {
        $q = [int][Math]::Round($PmRa * 10.0)
        if     ($q -gt  32767) { $pmRaTenths =  32767; $pmRaSat = $true }
        elseif ($q -lt -32767) { $pmRaTenths = -32767; $pmRaSat = $true }
        else                   { $pmRaTenths = $q }
    }
    if (-not [float]::IsNaN($PmDec)) {
        $q = [int][Math]::Round($PmDec * 10.0)
        if     ($q -gt  32767) { $pmDecTenths =  32767; $pmDecSat = $true }
        elseif ($q -lt -32767) { $pmDecTenths = -32767; $pmDecSat = $true }
        else                   { $pmDecTenths = $q }
    }
    if ($pmRaSat -or $pmDecSat) {
        # Either-axis saturation triggers a sidecar entry that carries the exact
        # pm (source * 10, int32) for BOTH components, so the reader can
        # substitute the lossless value when it sees a rail.
        [void]$OutputData.SidecarEntries.Add([PSCustomObject]@{
            Tyc1 = $Tyc1
            Tyc2 = $Tyc2
            Tyc3 = $Tyc3
            PmRaTenths  = if ([float]::IsNaN($PmRa))  { 0 } else { [int][Math]::Round($PmRa  * 10.0) }
            PmDecTenths = if ([float]::IsNaN($PmDec)) { 0 } else { [int][Math]::Round($PmDec * 10.0) }
        })
    }

    $pmRaInt16Bytes  = [BitConverter]::GetBytes([int16]$pmRaTenths)
    $pmDecInt16Bytes = [BitConverter]::GetBytes([int16]$pmDecTenths)
    if (-not $isLittleEndian) {
        [array]::Reverse($pmRaInt16Bytes)
        [array]::Reverse($pmDecInt16Bytes)
    }

    $entry = [byte[]]::new(17)
    $entry[0]  = $id2Bytes[0]; $entry[1]  = $id2Bytes[1]
    $entry[2]  = [byte]$Tyc3
    $entry[3]  = $raHBytes[0]; $entry[4]  = $raHBytes[1]; $entry[5]  = $raHBytes[2]; $entry[6]  = $raHBytes[3]
    $entry[7]  = $decBytes[0]; $entry[8]  = $decBytes[1]; $entry[9]  = $decBytes[2]; $entry[10] = $decBytes[3]
    $entry[11] = [byte]$vtDecimag
    $entry[12] = [byte]$btDecimag
    $entry[13] = $pmRaInt16Bytes[0];  $entry[14] = $pmRaInt16Bytes[1]
    $entry[15] = $pmDecInt16Bytes[0]; $entry[16] = $pmDecInt16Bytes[1]
    $stream.Write($entry, 0, 17)

    # track GSC region bounding box
    if ($RaHours -lt $OutputData.GscMinRA[$gscIdx])  { $OutputData.GscMinRA[$gscIdx]  = $RaHours }
    if ($RaHours -gt $OutputData.GscMaxRA[$gscIdx])  { $OutputData.GscMaxRA[$gscIdx]  = $RaHours }
    if ($DecDeg  -lt $OutputData.GscMinDec[$gscIdx]) { $OutputData.GscMinDec[$gscIdx] = $DecDeg }
    if ($DecDeg  -gt $OutputData.GscMaxDec[$gscIdx]) { $OutputData.GscMaxDec[$gscIdx] = $DecDeg }

    # accumulate HIP -> TYC mapping
    if ($Hip -ne 0) {
        if (-not $OutputData.HIPMap.ContainsKey($Hip)) {
            $OutputData.HIPMap[$Hip] = [System.Collections.Generic.List[short[]]]::new()
        }
        [void]$OutputData.HIPMap[$Hip].Add([short[]]@([short]$Tyc1, [short]$Tyc2, [short]$Tyc3))
    }
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

        # Same TryParse caveat as the magnitudes below: a failed parse writes 0 to the
        # out-param, silently defeating the -999 sentinel init. Branch on the bool and
        # restore -999 so an unparseable position is an obvious marker, not a star
        # masquerading at RA 0h / Dec 0 (which would also poison the GSC bounding box).
        [float]$ra = -999; [float]$dec = -999
        if ([float]::TryParse($values[$raIdx], $inv, [ref] $ra)) {
            $ra /= 15.0
        } else {
            $ra = -999
        }
        if (-not [float]::TryParse($values[$decIdx], $inv, [ref] $dec)) {
            $dec = -999
        }

        # Note: for posType 'X' entries, fields 24/25 are the observed Tycho-2 position
        # already in ICRS (J2000 reference frame). No precession is needed — ICRS coordinates
        # only differ by proper motion between epochs, and type 'X' entries have no proper motion.
        # Applying classical precession here would incorrectly rotate the coordinate frame,
        # producing errors of ~400" for Pleiades-era stars (e.g. Electra/TYC 1799-1441-1).

        # Parse VTmag (field 19) and BTmag (field 17), 0-indexed pipe-delimited
        # Encode as biased decimag: byte = clamp(round(mag * 10) + 20, 0, 254), 0xFF = missing
        # NOTE: float.TryParse writes 0 to its out-param on failure, so a blank field
        # would leave the mag at 0.0 (decimag 20 = mag 0.0), NOT NaN. Must branch on the
        # bool result and reset to NaN, otherwise a star with only BT (VT blank) bakes as
        # VT=0.0 and decodes to a bogus bright mag / huge B-V (e.g. TYC 9372-1058-1 ->
        # V=-1.15, B-V=10.88). Read the bool, don't trust the out-value.
        [float]$vtMag = [float]::NaN
        [float]$btMag = [float]::NaN
        if (-not [float]::TryParse($values[19].Trim(), $inv, [ref] $vtMag)) { $vtMag = [float]::NaN }
        if (-not [float]::TryParse($values[17].Trim(), $inv, [ref] $btMag)) { $btMag = [float]::NaN }

        # Parse pmRA (field 4) and pmDE (field 5) in mas/yr; both blank for posflg='X'
        # (mean position observed only, no proper motion derived). NaN propagates
        # through the Half cast cleanly.
        [float]$pmRa = [float]::NaN
        [float]$pmDec = [float]::NaN
        if ($posType -ne 'X') {
            [void][float]::TryParse($values[4].Trim(), $inv, [ref] $pmRa)
            [void][float]::TryParse($values[5].Trim(), $inv, [ref] $pmDec)
        }

        # Write the 17-byte binary entry (+ pm sidecar, GSC bbox, HIP map).
        Write-Tycho2BinaryEntry -Tyc1 $tyc1 -Tyc2 $tyc2 -Tyc3 $tyc3 `
            -RaHours $ra -DecDeg $dec -VtMag $vtMag -BtMag $btMag `
            -PmRa $pmRa -PmDec $pmDec -Hip $hip -OutputData $OutputData

        # accumulate HD -> TYC mapping (HD cross-ref is main-catalog only)
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

# Parses the Tycho-2 Supplement-1 (suppl_1.dat): the ~17,588 Hipparcos / Tycho-1
# stars that are NOT in the main Tycho-2 catalogue -- including the very brightest
# stars in the sky (Sirius, Vega, Antares, ...), which saturate the Tycho detector
# and are therefore relegated to the supplement. Without these the sky map renders
# no first-magnitude stars at all.
#
# suppl_1.dat is pipe-delimited (same as tyc2.dat as served by VizieR I/259).
# Positions are ICRS at epoch J1991.25 (NOT J2000 like the main catalogue!), so we
# propagate them forward to J2000 by proper motion -- a LINEAR pm shift, never
# precession (the frame is already ICRS; precession would wrongly rotate it). A
# Tycho-1-only star carries no pm, so its J1991.25 position is kept as-is (sub-
# arcsec error). The mflag column selects which magnitude fills the renderer's VT
# slot (see Note (3) in the ReadMe):
#   ' ' both BT+VT given -> VT in VT slot, BT in BT slot (true colour)
#   'V' only VT          -> VT in VT slot
#   'H' Hp instead of VT -> Hp in VT slot, BT blank -- this is how the brightest
#                           stars (e.g. Antares, Hp only) carry a magnitude
#   'B' only BT          -> BT in VT slot so the star still renders
function ConvertAndWrite-Tycho2Supplement1
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $UnzippedDataFileName,
        [Parameter(Mandatory = $true)] [PSCustomObject] $OutputData
    )

    $resolvedPath = (Resolve-Path $UnzippedDataFileName).Path
    $lines = [System.IO.File]::ReadAllLines($resolvedPath, [System.Text.Encoding]::ASCII)
    $inv = [cultureinfo]::InvariantCulture

    $dtYears = 2000.0 - 1991.25          # J1991.25 -> J2000.0 propagation interval
    $degPerMas = 1.0 / 3600000.0         # mas -> degrees
    $deg2Rad = [Math]::PI / 180.0

    $written = 0
    foreach ($line in $lines) {
        # Pipe-delimited fields (0-indexed):
        #   0 TYC "t1 t2 t3" | 1 flag[HT] | 2 RAdeg | 3 DEdeg | 4 pmRA | 5 pmDE
        #   | 6-9 errors | 10 mflag[ BVH] | 11 BTmag | 12 e_BT | 13 VTmag/Hp
        #   | 14 e_VT | 15 prox | 16 TYC[ T] | 17 HIP | 18 CCDM
        $values = $line.Split('|')
        if ($values.Length -lt 14) { continue }   # need at least through the VT field

        $tyc = ConvertFrom-TycIdComponents $values[0].Split(' ')
        $tyc1 = $tyc.Tyc1; $tyc2 = $tyc.Tyc2; $tyc3 = $tyc.Tyc3
        if ($tyc1 -lt 1 -or $tyc1 -gt $OutputData.Streams.Length) { continue }

        # ICRS @ J1991.25 (degrees).
        [float]$raDeg = [float]::NaN; [float]$decDeg = [float]::NaN
        if (-not [float]::TryParse($values[2].Trim(), $inv, [ref] $raDeg))  { continue }
        if (-not [float]::TryParse($values[3].Trim(), $inv, [ref] $decDeg)) { continue }

        # Proper motion (mas/yr, RA*cos(dec)); blank for Tycho-1 ('T') stars.
        [float]$pmRa = [float]::NaN; [float]$pmDec = [float]::NaN
        [void][float]::TryParse($values[4].Trim(), $inv, [ref] $pmRa)
        [void][float]::TryParse($values[5].Trim(), $inv, [ref] $pmDec)

        # Linear pm propagation J1991.25 -> J2000 (ICRS; no precession). Keep the
        # J1991.25 position when pm is unavailable (Tycho-1-only stars).
        [double]$raJ2000 = $raDeg
        [double]$decJ2000 = $decDeg
        if (-not [float]::IsNaN($pmDec)) {
            $decJ2000 = $decDeg + $pmDec * $dtYears * $degPerMas
        }
        if (-not [float]::IsNaN($pmRa)) {
            $cosDec = [Math]::Cos($decDeg * $deg2Rad)
            if ($cosDec -gt 1e-6) {
                $raJ2000 = $raDeg + ($pmRa * $dtYears * $degPerMas) / $cosDec
            }
        }
        if ($raJ2000 -lt 0)       { $raJ2000 += 360.0 }
        elseif ($raJ2000 -ge 360) { $raJ2000 -= 360.0 }

        [float]$raHours = [float]($raJ2000 / 15.0)
        [float]$decOut  = [float]$decJ2000

        # mflag selects the magnitude source for the renderer's VT slot:
        #   '' both -> VT slot = VT, BT slot = BT (true colour)
        #   'V'/'H' -> VT slot = VT/Hp, BT blank
        #   'B'     -> VT slot = BT so the star still renders
        $mflag = $values[10].Trim()
        [float]$btField = [float]::NaN; [float]$vtField = [float]::NaN
        [void][float]::TryParse($values[11].Trim(), $inv, [ref] $btField)
        [void][float]::TryParse($values[13].Trim(), $inv, [ref] $vtField)

        [float]$vtSlot = if ($mflag -eq 'B') { $btField } else { $vtField }
        [float]$btSlot = if ($mflag -eq '')  { $btField } else { [float]::NaN }

        $hip = 0
        if ($values.Length -ge 18) {
            [void][int]::TryParse($values[17].Trim(), $inv, [ref] $hip)
        }

        Write-Tycho2BinaryEntry -Tyc1 $tyc1 -Tyc2 $tyc2 -Tyc3 $tyc3 `
            -RaHours $raHours -DecDeg $decOut -VtMag $vtSlot -BtMag $btSlot `
            -PmRa $pmRa -PmDec $pmDec -Hip $hip -OutputData $OutputData
        $written++
    }

    Write-Host "  Supplement-1: wrote $written stars"
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

        # Tycho-2 Supplement-1: the bright/saturated Hipparcos+Tycho-1 stars that
        # are missing from the main catalogue (Sirius, Vega, Antares, ...). Same
        # VizieR catalogue (I/259) but a single un-split file.
        if ($folder -eq 'tyc2') {
            Write-Host "Downloading: https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/suppl_1.dat.gz"
            & curl -LO https://cdsarc.cds.unistra.fr/ftp/$($cat.Cat)/suppl_1.dat.gz
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
            Streams        = [System.IO.FileStream[]]::new($cat.StreamCount)
            HIPMap         = @{}
            HDMap          = @{}
            GscMinRA       = $gscMinRA
            GscMaxRA       = $gscMaxRA
            GscMinDec      = $gscMinDec
            GscMaxDec      = $gscMaxDec
            # Exact-pm overflow for the ~0.15% of stars whose |pm| > 254 mas/yr
            # (one-or-both axes). Sorted + written to tyc2_pm_sidecar.bin at end.
            SidecarEntries = [System.Collections.Generic.List[object]]::new()
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

        # After the main catalogue, fold in Supplement-1 (the bright stars missing
        # from tyc2.dat) -- extracted from suppl_1.dat.gz and written into the same
        # open GSC-region streams via the shared 17-byte entry writer.
        if ($folder -eq 'tyc2' -and $needsProcessing) {
            if (Test-Path 'suppl_1.dat.gz') {
                if (-not (Test-Path 'suppl_1.dat') -or $ForceDownload) {
                    Write-Host "Extracting suppl_1.dat.gz"
                    if (Test-Path 'suppl_1.dat') { Remove-Item 'suppl_1.dat' }
                    & 7z e -y 'suppl_1.dat.gz'
                }
                Write-Host "Processing Tycho-2 Supplement-1 (suppl_1.dat)"
                ConvertAndWrite-Tycho2Supplement1 -UnzippedDataFileName 'suppl_1.dat' -OutputData $outputData
            } else {
                Write-Warning "suppl_1.dat.gz not found -- bright stars (Sirius/Vega/Antares/...) will be MISSING. Run with -ForceDownload to fetch it."
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

            # PM sidecar: per-record entry = tyc1 u16 | tyc2 u16 | tyc3 u8
            #                              | pmRA int32 | pmDec int32 = 13 bytes.
            # int32 (= source * 10, no clipping) covers the full Tycho-2 pm
            # range including Barnard's 10277 mas/yr without further loss.
            # Sorted ascending by (tyc1, tyc2, tyc3) so the reader can binary
            # search OR fill a dictionary in one linear pass. No entry count
            # header -- count = decompressed length / 13. Reader is only
            # consulted when inline pmRA or pmDec == +/-32767 (the saturation
            # rails), so misses are functionally impossible at runtime.
            $sidecarEntries = @($outputData.SidecarEntries | Sort-Object Tyc1, Tyc2, Tyc3)
            $sidecarFile = [System.IO.Path]::Combine($PSScriptRoot, 'tyc2_pm_sidecar.bin')
            Write-Host "Writing pm sidecar to $sidecarFile ($($sidecarEntries.Count) saturated stars)"
            $sidecarBuffer = [byte[]]::new($sidecarEntries.Count * 13)
            $sidecarIdx = 0
            foreach ($e in $sidecarEntries) {
                $t1Bytes  = [BitConverter]::GetBytes([uint16]$e.Tyc1)
                $t2Bytes  = [BitConverter]::GetBytes([uint16]$e.Tyc2)
                $praBytes = [BitConverter]::GetBytes([int32]$e.PmRaTenths)
                $pdeBytes = [BitConverter]::GetBytes([int32]$e.PmDecTenths)
                if (-not $boundsIsLE) {
                    [array]::Reverse($t1Bytes);  [array]::Reverse($t2Bytes)
                    [array]::Reverse($praBytes); [array]::Reverse($pdeBytes)
                }
                $sidecarBuffer[$sidecarIdx]      = $t1Bytes[0];  $sidecarBuffer[$sidecarIdx + 1]  = $t1Bytes[1]
                $sidecarBuffer[$sidecarIdx + 2]  = $t2Bytes[0];  $sidecarBuffer[$sidecarIdx + 3]  = $t2Bytes[1]
                $sidecarBuffer[$sidecarIdx + 4]  = [byte]$e.Tyc3
                $sidecarBuffer[$sidecarIdx + 5]  = $praBytes[0]; $sidecarBuffer[$sidecarIdx + 6]  = $praBytes[1]
                $sidecarBuffer[$sidecarIdx + 7]  = $praBytes[2]; $sidecarBuffer[$sidecarIdx + 8]  = $praBytes[3]
                $sidecarBuffer[$sidecarIdx + 9]  = $pdeBytes[0]; $sidecarBuffer[$sidecarIdx + 10] = $pdeBytes[1]
                $sidecarBuffer[$sidecarIdx + 11] = $pdeBytes[2]; $sidecarBuffer[$sidecarIdx + 12] = $pdeBytes[3]
                $sidecarIdx += 13
            }
            [System.IO.File]::WriteAllBytes($sidecarFile, $sidecarBuffer)
            Compress-WithLzip $sidecarFile
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