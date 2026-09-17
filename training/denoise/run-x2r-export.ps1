# Arm X2R: the same seven pairs with the NIGHTS SWAPPED, so the independent target is the DEEPER night.
# Measured on the x2 cache first: night B is 1.2 to 4.7 times noisier than night A in every pair, so the
# x2/x2ctl contrast carries a second axis (target independence AND target depth). Reversing puts the
# quieter night on the target side, which flips the sign of that confound rather than removing it: if
# the independent target loses BOTH ways, the depth asymmetry is not what decided it.
$ErrorActionPreference = 'Stop'
$Tianwen = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll'
$Bake = 'D:/Astro-Dataset/2026-09-full'
$Export = 'D:\Astro-Dataset\pairs-x2r'
$camFilter = 'ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm'
$cam = 'ZWO ASI533MC Pro'
$filt = 'Optolong L-Ultimate 3nm'
function Sid([string]$folder, [string]$night, [string]$obj) { "$camFilter/$folder/$night|$cam|$obj|$filt" }
$rim = @('2025-05-02', '2026-02-16', '2026-02-18', '2026-02-20') | ForEach-Object { Sid 'Rim-Nebula' $_ 'Rim Nebula' }
$pairs = @()
for ($i = 0; $i -lt $rim.Count; $i++) {
    for ($j = $i + 1; $j -lt $rim.Count; $j++) { $pairs += "$($rim[$j])::$($rim[$i])" }
}
$pairs += (Sid 'Vela-SNR' '2026-01-23' 'Vela SNR') + '::' + (Sid 'Vela-SNR' '2025-12-17' 'Vela SNR')
$val = (Sid 'HD-74167' '2026-01-06' 'HD 74167') + '::' + (Sid 'HD-74167' '2026-01-05' 'HD 74167')
New-Item -ItemType Directory -Force $Export | Out-Null
"running $(Get-Date -Format o)" | Out-File (Join-Path $Export 'pairs.status') -Encoding utf8
$pairArgs = @(); foreach ($p in $pairs) { $pairArgs += '--pair'; $pairArgs += $p }
& dotnet $Tianwen dataset pair --bake $Bake --out $Export --cells 45 --seed 1 --psf-match `
    --inject-draws 8 --warp-sigma 0.5 @pairArgs *>> (Join-Path $Export 'pair.log')
if ($LASTEXITCODE -ne 0) { "failed" | Out-File (Join-Path $Export 'pairs.status') -Encoding utf8; throw "export failed" }
& dotnet $Tianwen dataset pair --bake $Bake --out $Export --cells 120 --seed 1 --psf-match `
    --inject-draws 8 --warp-sigma 0.5 --pair $val *>> (Join-Path $Export 'pair.log')
if ($LASTEXITCODE -ne 0) { "failed" | Out-File (Join-Path $Export 'pairs.status') -Encoding utf8; throw "val export failed" }
"done" | Out-File (Join-Path $Export 'pairs.status') -Encoding utf8
