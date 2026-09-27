# E14's read (run-poolrf.ps1, pre-registered): every model scored in one pass per domain on the eleven E13
# fields, with the same scorer, flags and field list as run-pool.ps1's step 5.
#   -Domain old  the existing eval caches, exported before the ring fix (the domain E12 and E13 were read in)
#   -Domain rf   their ring-fixed re-exports: each cache is looked up as <cache>-rf, and a cache named in
#                -Unchanged is scored as it stands because none of its masters' stretch moves with the fix
#                (that list is decided by measurement and recorded in the plan, never assumed)
#
# Run DETACHED; read the status file, never the log. Job files: C:\temp\e2\e14score-<domain>.{pid,status}.
#   $s = (Resolve-Path .\run-poolrf-score.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s,'-Domain','old' -WindowStyle Hidden
param(
    [ValidateSet('old', 'rf')][string]$Domain = 'old',
    [string[]]$Unchanged = @(),
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Cli = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.exe"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$status = Join-Path $LogDir "e14score-$Domain.status"
$PID | Out-File (Join-Path $LogDir "e14score-$Domain.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Final([string]$dir, [string]$name) {
    $f = Join-Path $dir "${name}_final.pt"
    if (Test-Path $f) { $f } else { Join-Path $dir "$name.pt" }
}
Set-Status 'starting'

try {
    $rf = Join-Path $Scratch 'n2n-pool-rf'
    $pool = Join-Path $Scratch 'n2n-pool'
    $ctl = Join-Path $Scratch 'n2n-bb-ctl'
    $models = @()
    foreach ($seed in 0..2) { $models += "poolrf_s$seed=$(Final $rf "poolrf_s$seed")" }
    foreach ($seed in 0..2) { $models += "pool_s$seed=$(Final $pool "pool_s$seed")" }
    foreach ($seed in 0..3) { $models += "conv_s$seed=$(Final $ctl "bb_ctlconv_s$seed")" }
    foreach ($seed in 0..5) { $models += "ctl4k_s$seed=$(Final $ctl "bb_ctl_s$seed")" }
    foreach ($m in $models) { if (-not (Test-Path ($m -split '=', 2)[1])) { throw "missing model $m" } }

    $env:TIANWEN_CLI = (Resolve-Path $Cli).Path
    "E14 score ($Domain) at $(git -C $PSScriptRoot rev-parse --short HEAD), scorer sha256 $((Get-FileHash n2n_starsplit.py).Hash.Substring(0, 12)), $($models.Count) models" |
        Out-File (Join-Path $LogDir "e14score-$Domain.log") -Encoding utf8

    $fields = [ordered]@{
        'n2n-bb-eval4'  = @('Small-Magellanic-Cloud/2026-08-01', 'Lagoon-and-Trifid/2023-08-03',
                            'Small-Magellanic-Cloud/2023-07-29', 'Carina-Wide/2025-03-19')
        'n2n-e2-eval4b' = @('HIP-34710/2025-12-28', 'HIP-85088/2025-05-20', 'V1045-Ori/2026-01-18',
                            'eta-Car-Nebula/2026-02-20')
        'n2n-eval4'     = @('RIM 135mm', 'Horsehead', 'Statue of Liberty')
    }
    $n = 0
    $failed = @()
    foreach ($c in $fields.Keys) {
        $cache = if ($Domain -eq 'rf' -and $c -notin $Unchanged) { "$c-rf" } else { $c }
        if (-not (Test-Path (Join-Path $Scratch "$cache\meta.json"))) { throw "no cache $cache under $Scratch" }
        if ($c -eq 'n2n-bb-eval4') {
            $env:TIANWEN_BAKES = $Bake
            $env:TIANWEN_SOLVED_MASTERS = Join-Path $LogDir 'gaia\solved-2026-09-25-full'
        }
        else {
            Remove-Item Env:TIANWEN_BAKES, Env:TIANWEN_SOLVED_MASTERS -ErrorAction SilentlyContinue
        }
        foreach ($f in $fields[$c]) {
            $n++
            Set-Status "score $n/11 $cache $f"
            & python n2n_starsplit.py --cache (Join-Path $Scratch $cache) --models @models --only $f --per-session `
                *> (Join-Path $LogDir "e14-score-$Domain-$c-$($f -replace '[/\\ ]', '_').txt")
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
