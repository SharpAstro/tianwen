# The detail-kept pass (docs/plans/denoiser-training.md, run log, "Detail kept"): every model E15 scored, again
# on E14's eleven ring-fixed fields, with n2n_starsplit.py's detail-kept block (added 2026-09-28 after E15's 1:1
# sheets showed every model smoothing the Orion Nebula's bright filaments while no scored column moved). No
# training and no prediction: this measures the models that exist, so the next arm has a baseline on the
# failure it is meant to fix.
#
# Run DETACHED; read the status file, never the log. Job files: C:\temp\e2\detail.{pid,status}; one output per
# field, C:\temp\e2\detail-score-<cache>-<field>.txt.
#   $s = (Resolve-Path .\run-detail-score.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Cli = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$status = Join-Path $LogDir 'detail.status'
$PID | Out-File (Join-Path $LogDir 'detail.pid') -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Final([string]$dir, [string]$name) {
    $f = Join-Path $dir "${name}_final.pt"
    if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
}
Set-Status 'starting'

try {
    $ctl = Join-Path $Scratch 'n2n-bb-ctl'
    $ctlrf = Join-Path $Scratch 'n2n-bb-ctl-rf'
    $models = @()
    foreach ($seed in 0..3) { $models += "convrf_s$seed=$(Final $ctlrf "bb_ctlrf_s$seed")" }
    foreach ($seed in 0..3) { $models += "conv_s$seed=$(Final $ctl "bb_ctlconv_s$seed")" }
    foreach ($seed in 0..2) { $models += "poolrf_s$seed=$(Final (Join-Path $Scratch 'n2n-pool-rf') "poolrf_s$seed")" }
    foreach ($seed in 0..2) { $models += "pool_s$seed=$(Final (Join-Path $Scratch 'n2n-pool') "pool_s$seed")" }
    foreach ($seed in 0..5) { $models += "ctl4k_s$seed=$(Final $ctl "bb_ctl_s$seed")" }
    $models += "shipped=$(Join-Path $Scratch 'n2n-e2-wide\e2_wide_s2.pt')"
    foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }
    $env:TIANWEN_CLI = (Resolve-Path $Cli).Path
    $env:TIANWEN_BAKES = $Bake
    $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
    "detail pass at $(git -C $PSScriptRoot rev-parse --short HEAD), scorer sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12)), $($models.Count) models" |
        Out-File (Join-Path $LogDir 'detail.log') -Encoding utf8
    $fields = [ordered]@{
        'n2n-bb-eval4-rf'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                               'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b-rf' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                               'eta-Car-Nebula/2026-02-20')
        'n2n-eval4-rf'     = @('Rim-Nebula/2025-05-02', 'Horsehead-Nebula/2025-10-28', 'Skull-and-Crossbones-Nebula/2026-02-14')
    }
    $n = 0
    $failed = @()
    foreach ($c in $fields.Keys) {
        foreach ($f in $fields[$c]) {
            $n++
            Set-Status "score $n/11 $c $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $c) --models @models --only $f `
                *> (Join-Path $LogDir "detail-score-$c-$($f -replace '[/\\ ]', '_').txt")
            if ($LASTEXITCODE -ne 0) { $failed += "$c $f (exit $LASTEXITCODE)" }
        }
    }
    $summary = if ($failed) { "failed: $($failed -join ', ')" } else { 'all eleven scored' }
    "done $(Get-Date -Format o): $summary" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
