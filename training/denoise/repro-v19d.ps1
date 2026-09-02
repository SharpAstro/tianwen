# E0 acceptance: the trainer in this directory reproduces the shipped checkpoint's weights bit for bit.
#
# n2n_v19d_s2_final.pt (2026-08-16, D:\Astro-Dataset\n2n-smoke\v19, GTX 1070) is the model that
# ships as tianwen_denoise_osc_v19d.onnx. Re-running its recipe from the same prepared cache with
# the same seed must give the same weights: --seed fixes the init and the tile draw, and cuDNN runs
# deterministic. Anything else means the port, or the environment (torch build, driver), changed
# the trainer, and every later comparison against v19d would be measuring that change rather than
# an arm.
#
# The comparison is tensor for tensor (n2n_ckpt_equal.py), NOT a file hash. The first version of
# this script hashed the files and said DIFFERENT for a run that had reproduced all 813,251
# parameters of both checkpoints exactly (2026-09-02): torch.save names every archive member after
# the output file's stem (n2n_v19d_s2/data.pkl) and pickles whatever metadata the trainer passes,
# and the ported trainer records one more key (pair_time) than the August one did. Identical
# weights under a different name are a different file by construction. The hashes are still
# written to the log, as a record and not as the verdict.
#
# The trainer saves --out INSIDE the cache directory, so the reproduction uses its own name and
# never touches the reference files it is compared against. Both checkpoints are compared: the
# gate-selected one (--out) and the last step's (_final).
#
# Run detached, and read the status file rather than the log while it runs:
#   Start-Process pwsh -ArgumentList '-NoProfile','-File',"$PWD\repro-v19d.ps1",'-LogDir','C:\temp\e0'
# -CompareOnly skips the 12-minute training and re-judges the checkpoints already in the cache.
param(
    [string]$Cache = (Join-Path ($env:TIANWEN_SCRATCH ?? 'C:\temp\tianwen-scratch') 'n2n-d8'),
    [string]$Reference = 'n2n_v19d_s2_final.pt',
    [string]$Out = 'e0-repro_v19d_s2.pt',
    [string]$LogDir = $PSScriptRoot,
    [switch]$CompareOnly
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force $LogDir | Out-Null
$log = Join-Path $LogDir 'repro-v19d.log'
$status = Join-Path $LogDir 'repro-v19d.status'
"running $(Get-Date -Format o)" | Out-File $status -Encoding utf8
try {
    if (-not $CompareOnly) {
        & python n2n_smoke.py --train --cache $Cache `
            --loss l2 --upsample --mix-avg --cond `
            --band-loss 3 --band-scales "2,4 4,8" `
            --base 32 --steps 4000 --gate-every 100 `
            --seed 2 --out $Out *>&1 | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "training exited with $LASTEXITCODE" }
    }

    $final = $Out -replace '\.pt$', '_final.pt'
    $selectedReference = $Reference -replace '_final\.pt$', '.pt'
    $pairs = @(
        @{ label = 'final';    reference = $Reference;         reproduced = $final },
        @{ label = 'selected'; reference = $selectedReference; reproduced = $Out }
    )
    $identical = $true
    foreach ($pair in $pairs) {
        foreach ($role in 'reference', 'reproduced') {
            $name = $pair[$role]
            $sha = (Get-FileHash (Join-Path $Cache $name) -Algorithm SHA256).Hash
            "$($pair.label.PadRight(9)) $($role.PadRight(10)) $sha  $name" | Tee-Object -FilePath $log -Append
        }
        & python n2n_ckpt_equal.py --cache $Cache $pair.reference $pair.reproduced *>&1 | Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -eq 1) { $identical = $false }
        elseif ($LASTEXITCODE -ne 0) { throw "n2n_ckpt_equal.py exited with $LASTEXITCODE on the $($pair.label) pair" }
    }
    $verdict = if ($identical) { 'IDENTICAL' } else { 'DIFFERENT' }
    "verdict   $verdict  (weights and shared metadata, tensor for tensor; the hashes above are the record, not the test)" | Tee-Object -FilePath $log -Append
    "$verdict $(Get-Date -Format o)" | Out-File $status -Encoding utf8
}
catch {
    "FAILED $_ $(Get-Date -Format o)" | Out-File $status -Encoding utf8
    throw
}
