# Waits for E2.8c (C:\temp\e2\p2-star-c.status turning done or failed) so the card is free, then runs
# the D1 output probe launcher. The probe's own status file (d1-output-rim.status) is the one to watch.
$ErrorActionPreference = 'Stop'
$gate = 'C:\temp\e2\p2-star-c.status'
while (-not (Test-Path $gate) -or -not ((Get-Content $gate -Raw) -match '^(done|failed)')) {
    Start-Sleep -Seconds 30
}
& (Join-Path $PSScriptRoot 'run-d1-output-rim.ps1')
