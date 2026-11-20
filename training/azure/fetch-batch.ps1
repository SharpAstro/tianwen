# Brings home what a Batch run (launch-batch.ps1) left in the container, whenever a machine is next around:
#   pwsh -File fetch-batch.ps1 -Tag r3xaz -Name r3x_synth16_s0 -FetchTo D:/temp/tianwen-scratch/r2-synth16
# Says where the job stands (Batch's own word, and the heartbeat the task synced), fetches the log always and the
# weights when they are there, and checks the weights load. It changes nothing in Azure: the job's pool was deleted by
# Batch when the job ended, and the job record itself costs nothing.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string]$FetchTo
)
. (Join-Path $PSScriptRoot 'common.ps1')
$sas = Get-Sas
$base = Get-ContainerUrl "r2-runs/$Tag"
& $AzCli batch account login -n $AzConfig.batchAccount -g $AzConfig.resourceGroup --subscription $AzConfig.subscription | Out-Null
"job: $(& $AzCli batch job show --job-id $Tag --query '[state, executionInfo.terminateReason]' -o tsv 2>$null)"
"task: $(& $AzCli batch task show --job-id $Tag --task-id train --query '[state, executionInfo.result, executionInfo.exitCode, executionInfo.retryCount]' -o tsv 2>$null)"
"pools still up: $(@(& $AzCli batch pool list --query "[?starts_with(id, '$Tag')].id" -o tsv).Count)"
$dir = Join-Path $RunsDir "$Tag-batch"
New-Item -ItemType Directory -Force $dir | Out-Null
foreach ($f in 'heartbeat.txt', "$Tag.log") {
    & $AzCopy copy "$base/$f`?$sas" (Join-Path $dir $f) --overwrite true --output-level quiet 2>&1 | Out-Null
}
if (Test-Path (Join-Path $dir 'heartbeat.txt')) { "heartbeat: $(Get-Content (Join-Path $dir 'heartbeat.txt'))" }
if (Test-Path (Join-Path $dir "$Tag.log")) { Get-Content (Join-Path $dir "$Tag.log") -Tail 4 }
# The run's weights, then its best step's (a run with --save-best in its TRAIN_FLAGS; task.sh uploads those first, so
# they are there whenever the run's own are).
foreach ($file in "$Name.pt", "$Name.best.pt") {
    $listing = & $AzCopy list "$base/$file`?$sas" --machine-readable 2>&1 | Out-String
    if ($listing -notmatch 'Content Length: (\d+)') {
        if ($file -eq "$Name.pt") { "no $file in the container yet"; return }
        "no $file in the container: the run kept no best step's weights"
        continue
    }
    $size = [long]$Matches[1]
    $out = Join-Path $FetchTo $file
    & $AzCopy copy "$base/$file`?$sas" $out --overwrite true --output-level essential | Out-Null
    if ($LASTEXITCODE -ne 0 -or (Get-Item $out).Length -ne $size) { throw "$file did not come home at $size bytes" }
    & python -I -c "import sys, torch; ck = torch.load(sys.argv[1], map_location='cpu', weights_only=False); print('loads:', len(ck['model']), 'tensors, selected at step', ck.get('selected_at_step'))" $out
    if ($LASTEXITCODE -ne 0) { throw "$out does not load" }
    "$out ($size bytes)"
}
