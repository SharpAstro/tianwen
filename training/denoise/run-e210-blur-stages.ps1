# Where does a stack's width come from? Every stack of the Orion night (thirds, whole night, drizzle)
# measures 2.7 to 2.8 px on green from subs the green fit puts at 1.7 to 2.5. Three stacks from the
# whole-night manifest isolate the stages: the reference frame ALONE (identity transform, so no
# resampling: what calibration + debayer + the writer do), the reference plus one sharp frame (one
# bilinear warp averaged in), the reference plus two. Same grouping flags as the pair; masters cache
# copied so calibration is not rebuilt. Read with SeeingSplitDiagnosticProbe (exp-*/ masters).
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2'
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-blur-stages.status'
$log = Join-Path $LogDir 'e210-blur-stages.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
try {
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    foreach ($label in @('ref1', 'ref-plus-1', 'ref-plus-2')) {
        $manifest = Join-Path $Out "master_GreatOrionNebula_light_120s_12C_g120-$label.manifest.json"
        $dir = Join-Path $Out "exp-$label"
        New-Item -ItemType Directory -Force $dir | Out-Null
        if (Test-Path (Join-Path $Out 'masters')) { Copy-Item (Join-Path $Out 'masters') $dir -Recurse -Force }
        "=== $label" | Out-File $log -Append -Encoding utf8
        & $exe stack $Archive -o $dir --group-filter GreatOrionNebula_light_120s_1 --group-temp-tolerance 2 --strategy Float16Staged `
            --no-plate-solve --output-format none --manifest $manifest *>> $log
        if ($LASTEXITCODE -ne 0) { throw "tianwen stack ($label) exited $LASTEXITCODE" }
    }
    "done $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "failed $(Get-Date -Format o): $_" | Out-File $status -Encoding utf8
    throw
}
