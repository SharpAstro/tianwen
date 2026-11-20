# R4g's extra export (docs/plans/star-remover-training.md, "R4g"): the listed bright cells of the sessions a cell list names,
# and no sample, with the stars drawn on the stored starless PLATE (--background plate), so the target keeps the nebula's own
# edges and knots. The list comes from bright_plate_cells.py. The star line is export-synth.ps1's without what only a
# synthetic background takes (--steer, --nebula-knots, --bright-knots-wide, --faint-stars: a plate holds its own faint stars).
#
# Then, on the export: `tianwen dataset plate-masks --export <Root>/<Name>` writes each draw's loss mask, and the trainer packs
# them with --prepare-keep and reads them with --extra-loss-mask.
#
# Resumable by chunk as export-synth.ps1 is; stop it between chunks with export.stop in -Root, never by stopping the process.
# Run DETACHED, with no window (from a PowerShell session: Start-Process pwsh -ArgumentList ... -NoNewWindow).
#   pwsh -NoProfile -File export-bright-plates.ps1 -Root D:/temp/tianwen-scratch/r3-train-bright1 -Name bright1 `
#        -Bin D:/temp/tianwen-scratch/bh-bin11/tianwen.exe -Cells D:/temp/tianwen-scratch/r4g/bright-plate-cells.tsv `
#        -Extra '--saturated-own-share','--saturated-measured','--halo-over-plate','--noise-as-recorded'
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Bin,
    # The cell list (x TAB y TAB session id; '#' lines are comments). Its sessions are the export's.
    [Parameter(Mandatory)][string]$Cells,
    [string[]]$Extra = @(),
    [string]$BetaDraw = '0.3,1.3,6,3',
    [int]$Draws = 20,
    [string]$Store = 'D:/Astro-Dataset/2026-10-12-full',
    [int]$Chunk = 4
)
$ErrorActionPreference = 'Stop'
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
    # Through `pwsh -File` an array arrives as one comma-joined string, which the CLI refuses as one unknown option.
    $Extra = @($Extra | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $list = @(Get-Content $Cells | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { ($_ -split "`t")[2] } | Sort-Object -Unique)
    if ($list.Count -eq 0) { Say "FAILED: $Cells names no session"; return }
    "bin $Bin; cells $Cells; draws $Draws; beta draw $BetaDraw; extra $($Extra -join ' '); background plate" | Out-File "$Root/export.recipe" -Encoding utf8

    $common = @('dataset', 'degrade', '--bake', "`"$Store`"", '--plates', "`"$Store/starless`"", '--mode', 'stars', '--shape', 'warped',
        '--warp-sigma', '0.5', '--warp-sigma-drizzle', '0', '--cells', '40', '--draws', "$Draws", '--seed', '1',
        '--background', 'plate', '--stretch-from', 'master', '--star-beta-draw', $BetaDraw,
        '--extra-cells', "`"$Cells`"", '--listed-only') + $Extra + @('--out', "`"$out`"")
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
