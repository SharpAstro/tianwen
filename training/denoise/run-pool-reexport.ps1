# E13's pool, re-exported after the canvas-ring fix (issue #489; the commit "fix(ai): the NAFNet pre-stretch
# measures covered pixels only, and hands the canvas ring back untouched"). Every export before that fix
# stretched a master with a zero canvas ring from a floor of 0, so a model trained on it no longer matches
# the fixed inference (docs/known-limitations.md). The same sessions (arms/pool-train.txt), the same val pair
# (arms/bb-val-2.txt) and the same degrade flags as run-pool.ps1, into a NEW store, then one prepared cache
# (n2n-pool-rf), so the model to ship trains on data that matches inference. Export and prepare only: CPU and
# disk, safe beside a training run. The store sits on C: (the SSD) rather than beside the bake on the USB
# disk: about 19 GB of tiles written and then read straight back by the prepare.
#
# Run DETACHED; read the status file, never the log. The heartbeat finds it by C:\temp\e2\poolrf.pid.
#   $s = (Resolve-Path .\run-pool-reexport.ps1).Path
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',$s -WindowStyle Hidden
# To stop it between stages, create C:\temp\e2\poolrf.stop; NEVER stop the process itself (it owns its
# child's output pipe).
param(
    [string]$Bake = 'D:/Astro-Dataset/2026-09-25-full',
    [string]$Export = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'degraded\pool-vshape-ringfix'),
    [string]$Scratch = ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch'),
    [string]$LogDir = 'C:\temp\e2',
    [string]$Tianwen = "$PSScriptRoot\..\..\src\TianWen.Cli\bin\Release\net10.0\tianwen.dll",
    # A later re-export (the bake changed under a session) gets its own cache and its own status files,
    # so it never overwrites the cache a run is training from.
    [string]$Cache = 'n2n-pool-rf',
    [string]$Tag = 'poolrf'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Join-Path $LogDir "$Tag.status"
$log = Join-Path $LogDir "$Tag.log"
$stopFile = Join-Path $LogDir "$Tag.stop"
$PID | Out-File (Join-Path $LogDir "$Tag.pid") -Encoding ascii
function Set-Status([string]$what) { "running $(Get-Date -Format o): $what" | Out-File $status -Encoding utf8 }
function Read-List([string]$path) {
    Get-Content $path | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }
}
function Stop-Requested([string]$before) {
    if (Test-Path $stopFile) {
        "stop file present before $before; ending here" | Tee-Object -FilePath $log -Append
        "stopped $(Get-Date -Format o): before $before" | Out-File $status -Encoding utf8
        return $true
    }
    return $false
}
Set-Status 'starting'

try {
    if (-not (Test-Path $Tianwen)) { throw "no CLI at $Tianwen; build TianWen.Cli in Release first" }
    # The binary must contain the fix, or this is the old export again under a new name. Found by SUBJECT,
    # which survives the rebase a hash would not.
    $fix = git -C $PSScriptRoot log -1 --format=%cI --grep 'the NAFNet pre-stretch measures covered pixels only'
    if (-not $fix) { throw 'the ring-fix commit is not in this checkout' }
    $built = (Get-Item $Tianwen).LastWriteTime
    if ($built -lt [datetimeoffset]::Parse($fix).LocalDateTime) {
        throw "the CLI was built $($built.ToString('o')), before the ring fix ($fix); rebuild TianWen.Cli in Release"
    }
    "E13 pool re-export at $(git -C $PSScriptRoot rev-parse --short HEAD), CLI built $($built.ToString('o')), ring fix committed $fix" |
        Tee-Object -FilePath $log -Append

    # 1. Export, exactly as run-pool.ps1 does: the val pair at 120 cells, then the pool at 48, one
    #    varied-shape setting for both. A finished stage is skipped on re-run.
    $shapeArgs = @('--mode', 'noise', '--shape', 'warped', '--warp-sigma', '0', '--warp-sigma-max', '0.7',
                   '--white-fraction', '0.25', '--draws', '8', '--seed', '1')
    New-Item -ItemType Directory -Force $Export | Out-Null
    foreach ($stage in @(@{ Name = 'val'; List = 'arms\bb-val-2.txt'; Cells = 120 },
                         @{ Name = 'pool'; List = 'arms\pool-train.txt'; Cells = 48 })) {
        if (Stop-Requested "export $($stage.Name)") { return }
        $stageStatus = Join-Path $Export "degrade-$($stage.Name).status"
        if ((Test-Path $stageStatus) -and ((Get-Content $stageStatus -Raw).Trim() -eq 'done')) {
            "export $($stage.Name): done, skipped" | Tee-Object -FilePath $log -Append
            continue
        }
        Set-Status "export $($stage.Name)"
        $sessionArgs = @()
        foreach ($sid in (Read-List $stage.List)) { $sessionArgs += '--session'; $sessionArgs += $sid }
        # Built by appending, never as `if (...) { @('x') }`, which unrolls to a string splatted per character.
        $measure = @()
        if ($stage.Name -eq 'pool') { $measure += '--measure-shape' }
        "export $($stage.Name) -> $Export ($($sessionArgs.Count / 2) sessions, $($stage.Cells) cells)" | Tee-Object -FilePath $log -Append
        "running" | Out-File $stageStatus -Encoding utf8
        & dotnet $Tianwen dataset degrade --bake $Bake --out $Export @shapeArgs --cells $stage.Cells @measure @sessionArgs `
            *>> (Join-Path $Export "degrade-$($stage.Name).log")
        if ($LASTEXITCODE -ne 0) { "failed" | Out-File $stageStatus -Encoding utf8; throw "export $($stage.Name) failed (exit $LASTEXITCODE)" }
        "done" | Out-File $stageStatus -Encoding utf8
        Get-Content (Join-Path $Export "degrade-$($stage.Name).log") | Select-String '^\[degrade\]' | ForEach-Object { $_.Line } |
            Tee-Object -FilePath $log -Append
    }

    # 2. Prepare the cache a post-fix arm trains from, with run-pool.ps1's own selection.
    if (Stop-Requested 'prepare') { return }
    $cache = Join-Path $Scratch $Cache
    if (Test-Path (Join-Path $cache 'meta.json')) {
        "prepare: $cache present, skipped" | Tee-Object -FilePath $log -Append
    }
    else {
        Set-Status "prepare $Cache"
        & python n2n_smoke.py --prepare --root $Export --cache $cache `
            --train-from-list arms\pool-train.txt --val-from-list arms\bb-val-2.txt `
            --cells-per-session 45 --val-cells-per-session 120 *>> $log
        if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
    }
    "done $(Get-Date -Format o): export and cache ready at $cache" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    $_ | Out-String | Tee-Object -FilePath $log -Append
    throw
}
