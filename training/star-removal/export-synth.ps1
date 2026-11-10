# A star remover's training export (docs/plans/star-remover-training.md, R3): `tianwen dataset degrade --mode stars` over the
# training sessions in chunks of ten, with synth18's recipe (R3l's) and the arm's own options after it. Resumable: a chunk
# whose " <tag> exit 0" line is in export.log is skipped. Writes export.done at the end; stop it between chunks with
# export.stop in -Root, never by stopping the process.
#
# The recipe, written once here so every arm's export is this line plus its -Extra options:
#   --mode stars --shape warped --warp-sigma 0.5 --warp-sigma-drizzle 0 --cells 40 --draws 4 --seed 1
#   --background synthetic --steer 0.5,2,0.8,1 --nebula-knots --bright-knots-wide --stretch-from master
#   --star-beta-draw 0.3,1.3,6
# synth18 (R3l) is that line alone; synth20 (R3q) added --saturated-own-share --saturated-measured at "a measured saturated
# star's amplitude floor stands on its own sky"; R3r's export adds the same two at the fixed injector (the plan's R3q/R3r
# section). Record the binary's commit beside the export (-Bin's folder holds HEAD.txt when it was built for one).
#
# Run DETACHED, with no window (from a PowerShell session: Start-Process pwsh -ArgumentList ... -NoNewWindow).
#   pwsh -NoProfile -File export-synth.ps1 -Root D:/temp/tianwen-scratch/r3-train-synth21 -Name synth21 `
#        -Bin C:/temp/tianwen-scratch/bh-bin6/tianwen.exe -Extra '--saturated-own-share','--saturated-measured'
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Bin,
    [string[]]$Extra = @(),
    [string]$Store = 'D:/Astro-Dataset/2026-10-12-full',
    # The 138 training sessions synth4 to synth20 were exported over, one session id per line.
    [string]$Sessions = (Join-Path $PSScriptRoot 'synth-sessions.txt'),
    [int]$Chunk = 10
)
$ErrorActionPreference = 'Stop'
$plates = "$Store/starless"
$out = "$Root/$Name"
$exportLog = "$Root/export.log"
$status = "$Root/export.status"
$log = "$Root/export-chain.log"
$stop = "$Root/export.stop"
$done = "$Root/export.done"
function Say([string]$what) { "$(Get-Date -Format o) $what" | Out-File $log -Append -Encoding utf8; "$what $(Get-Date -Format o)" | Out-File $status -Encoding utf8 }

New-Item -ItemType Directory -Force $Root | Out-Null
if (Test-Path $done) { return }
if (Test-Path $stop) { return }

try {
    if (-not (Test-Path $exportLog)) { New-Item -ItemType File $exportLog | Out-Null }
    $list = @(Get-Content $Sessions | Where-Object { $_ })
    if ($list.Count -ne 138) { Say "FAILED: $($list.Count) sessions, the training set has 138"; return }
    "bin $Bin; extra $($Extra -join ' ')" | Out-File "$Root/export.recipe" -Encoding utf8

    $common = @('dataset', 'degrade', '--bake', "`"$Store`"", '--plates', "`"$plates`"", '--mode', 'stars', '--shape', 'warped',
        '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', '4', '--seed', '1',
        '--background', 'synthetic', '--steer', '0.5,2,0.8,1', '--nebula-knots', '--bright-knots-wide', '--stretch-from', 'master',
        '--star-beta-draw', '0.3,1.3,6') + $Extra + @('--out', "`"$out`"")
    for ($i = 0; $i -lt $list.Count; $i += $Chunk) {
        $tag = '{0}-{1:D3}' -f $Name, $i
        if (Select-String -Path $exportLog -SimpleMatch " $tag exit 0" -Quiet) { continue }
        if (Test-Path $stop) { Say "stopped before $tag"; return }
        $a = $common
        foreach ($s in $list[$i..([Math]::Min($i + $Chunk, $list.Count) - 1)]) { $a += '--session'; $a += "`"$s`"" }
        "$(Get-Date -Format o) $tag start" | Out-File $exportLog -Append -Encoding utf8
        Say "export $tag"
        $p = Start-Process -FilePath $Bin -ArgumentList $a -RedirectStandardOutput "$Root/export-$tag.log" `
            -RedirectStandardError "$Root/export-$tag.err.log" -NoNewWindow -PassThru
        $null = $p.Handle  # held, or ExitCode reads empty once the process is gone
        # Normal priority: a BelowNormal chunk starves on a busy machine.
        $p.WaitForExit()
        "$(Get-Date -Format o) $tag exit $($p.ExitCode)" | Out-File $exportLog -Append -Encoding utf8
        if ($p.ExitCode -ne 0) { Say "FAILED: export $tag exited $($p.ExitCode)"; New-Item -ItemType File $stop -Force | Out-Null; return }
    }
    "exported $(Get-Date -Format o)" | Out-File $done -Encoding utf8
    Say "done: $Name exported"
}
catch {
    Say "FAILED: $_"
    throw
}
