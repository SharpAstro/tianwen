# A prepared cache (or any folder) to a folder of the training container, resumable, for a run on an Azure T4:
#   pwsh -File upload-cache.ps1 -Source D:/tianwen-scratch/r2-synth7 -Blob r2-synth7/cache -CapMbps 10
# Small files go through azcopy held to 8 connections; a file of 1 GB or more through blockupload.py, as numbered blocks
# a restart continues. The first synth7 upload (2026-10-21) taught both: azcopy's default 160 connections on a ~14 Mb/s
# uplink timed every chunk out and sent it again (1,130 retries), and azcopy restarts a 21 GB file from zero whenever
# its pass is restarted. -CapMbps keeps room on the uplink (the owner chose 10 of about 14). *.pt files (a trainer's own
# checkpoints in the cache folder) stay home. Resumable as a pass: run it again, or from a scheduled task every 10
# minutes (conhost --headless, priority 4, IgnoreNew); a pass exits at once when the upload is done or another pass for
# this -Blob still runs. Writes upload-<blob>.done in the runs folder (common.ps1) once every file is in the container
# at its size.
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Blob,
    [int]$CapMbps = 0
)
. (Join-Path $PSScriptRoot 'common.ps1')
$tag = 'upload-' + ($Blob.Trim('/') -replace '[/\\]', '-')
$log = Join-Path $RunsDir "$tag.log"
$done = Join-Path $RunsDir "$tag.done"
if (Test-Path $done) { return }
$mine = [regex]::Escape($Blob.Trim('/'))
if (Test-Running "azcopy.*$mine|blockupload\.py.*$mine") { return }
function Say([string]$what) { "$(Get-Date -Format o) $what" | Tee-Object -FilePath $log -Append }

$sas = Get-Sas
$base = Get-ContainerUrl $Blob
$env:AZCOPY_CONCURRENCY_VALUE = '8'
$cap = if ($CapMbps -gt 0) { @('--cap-mbps', "$CapMbps") } else { @() }
$files = Get-ChildItem $Source -File | Where-Object { $_.Extension -ne '.pt' } | Sort-Object Length
foreach ($f in $files) {
    Say "upload $($f.Name) ($([math]::Round($f.Length / 1GB, 2)) GB)$(if ($CapMbps -gt 0) { ", capped at $CapMbps Mb/s" })"
    if ($f.Length -ge 1GB) {
        & python -I (Join-Path $PSScriptRoot 'blockupload.py') $f.FullName "$base/$($f.Name)" $AzConfig.sasFile --cap-mbps $CapMbps *>> $log
    }
    else {
        & $AzCopy copy $f.FullName "$base/$($f.Name)?$sas" --overwrite false @cap --output-level essential *>> $log
    }
    if ($LASTEXITCODE -ne 0) { Say "FAILED: $($f.Name) exited $LASTEXITCODE"; exit 1 }
}
# Proof: every file listed in the container at its local size.
$listing = & $AzCopy list "$base`?$sas" --machine-readable 2>&1 | Out-String
foreach ($f in $files) {
    # azcopy 10.32 lists "name; Content Length: 200"; older versions prefixed "INFO: ".
    if ($listing -notmatch "(?m)^(INFO: )?$([regex]::Escape($f.Name));\s+Content Length: $($f.Length)\s*$") { Say "FAILED: $($f.Name) is not in the container at $($f.Length) bytes"; exit 1 }
}
Say "done: $($files.Count) files in $Blob at their local sizes"
"done $(Get-Date -Format o)" | Out-File $done -Encoding utf8
