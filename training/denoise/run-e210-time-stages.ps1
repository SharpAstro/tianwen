# Does the stack's blur grow with frame COUNT or with TIME from the reference? Four stacks from the
# whole-night manifest: the 6 frames nearest the reference in time (span 0.33 h), the 6 farthest plus
# the reference (span 3.0 h), the nearest 12 (0.9 h) and the nearest 24 (1.9 h). Read with
# SeeingSplitDiagnosticProbe (exp-*/ masters). Same flags as the pair; calibration cache copied.
param(
    [string]$Repo = 'C:\Users\SebastianGodelet\source\repos\sharpastro\tianwen',
    [string]$Archive = 'D:\Astro-Organized',
    [string]$Out = 'C:\temp\e2\e210-orion',
    [string]$LogDir = 'C:\temp\e2',
    [string[]]$Labels = @('near6', 'far6', 'near12', 'near24')
)
$ErrorActionPreference = 'Stop'
$status = Join-Path $LogDir 'e210-time-stages.status'
$log = Join-Path $LogDir 'e210-time-stages.log'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
$exe = Join-Path $Repo 'src\TianWen.Cli\bin\Release\net10.0\tianwen.exe'
try {
    "$(& $exe --version 2>&1 | Select-Object -First 1)" | Out-File $log -Encoding utf8
    foreach ($label in $Labels) {
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
