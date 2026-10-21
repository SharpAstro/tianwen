# Dot-sourced by every training/azure script. This repository is public, so nothing here names the subscription, the
# resource group or the storage account: they are read from a user-only settings file outside the repository
# ($HOME/.azure-upload/tw-training.json, or the file TIANWEN_AZURE_CONFIG names), beside the SAS file, which is never
# written anywhere else (README.md, "The guards").
$ErrorActionPreference = 'Stop'

$AzConfigPath = if ($env:TIANWEN_AZURE_CONFIG) { $env:TIANWEN_AZURE_CONFIG } else { Join-Path $HOME '.azure-upload/tw-training.json' }
if (-not (Test-Path $AzConfigPath)) { throw "no Azure training settings at $AzConfigPath (README.md, 'Settings')" }
$AzConfig = Get-Content $AzConfigPath -Raw | ConvertFrom-Json
foreach ($key in 'subscription', 'resourceGroup', 'location', 'storageSubscription', 'storageResourceGroup', 'account', 'container',
    'sasFile', 'sshKey', 'vmSize', 'maxPrice', 'image', 'tags') {
    if ($null -eq $AzConfig.$key -or "$($AzConfig.$key)" -eq '') { throw "$AzConfigPath does not name '$key'" }
}

# Where a run's status, log, stop and done files and its fetched files go. Every run before the move into the repository
# kept them in C:/temp/e2, which TIANWEN_RUNS overrides.
$RunsDir = if ($env:TIANWEN_RUNS) { $env:TIANWEN_RUNS } else { 'C:/temp/e2' }
$AzCopy = (Get-Command azcopy -ErrorAction Stop).Source
$AzCli = (Get-Command az -ErrorAction Stop).Source
$env:AZCOPY_LOG_LOCATION = Join-Path $RunsDir 'azcopy/logs'
$env:AZCOPY_JOB_PLAN_LOCATION = Join-Path $RunsDir 'azcopy/plans'

function Get-ContainerUrl([string]$path = '') {
    $u = "https://$($AzConfig.account).blob.core.windows.net/$($AzConfig.container)"
    if ($path) { "$u/$($path.TrimStart('/'))" } else { $u }
}
function Get-Sas {
    if (-not (Test-Path $AzConfig.sasFile)) { throw "no SAS at $($AzConfig.sasFile): mint one with New-RunSas (launch-arm.ps1 -MintSas)" }
    (Get-Content $AzConfig.sasFile -Raw).Trim().TrimStart('?')
}
# A container SAS (read, write, list) for $days days into the user-only file the guards name; deleting the file does not
# revoke it, so the expiry is the backstop.
function New-RunSas([int]$days = 3) {
    $key = & $AzCli storage account keys list --subscription $AzConfig.storageSubscription -g $AzConfig.storageResourceGroup -n $AzConfig.account --query '[0].value' -o tsv
    $expiry = (Get-Date).ToUniversalTime().AddDays($days).ToString('yyyy-MM-ddTHH:mmZ')
    $sas = & $AzCli storage container generate-sas --account-name $AzConfig.account --account-key $key -n $AzConfig.container --permissions rwl --expiry $expiry --https-only -o tsv
    if (-not $sas) { throw 'could not mint the SAS' }
    New-Item -ItemType Directory -Force (Split-Path $AzConfig.sasFile) | Out-Null
    Set-Content -Path $AzConfig.sasFile -Value $sas -NoNewline -Encoding ascii
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    icacls $AzConfig.sasFile /inheritance:r /grant:r "$($me):(R,W)" | Out-Null
    if ($LASTEXITCODE -ne 0) { Remove-Item $AzConfig.sasFile -Force; throw "could not make the SAS file user-only for $me" }
    $expiry
}
function Test-Running([string]$pattern) { @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match $pattern }).Count -gt 0 }
