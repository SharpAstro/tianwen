# One training arm as an Azure Batch job that removes itself (README.md, "A run that removes itself"):
#   pwsh -File launch-batch.ps1 -Tag r3xaz -Name r3x_synth16_s0 -Seed 0 -Run C:/temp/e2/r3h-run `
#       -CacheBlob r2-synth16/cache -TrainFlags '--levels 4 --max-ch 256 --clip-grad 8'
# launch-arm.ps1 makes a VM that this desktop's scheduled task has to babysit and delete. This makes a Batch JOB instead:
# a pool of one low-priority T4 node that exists for the job alone (autoPoolSpecification, poolLifetimeOption job,
# keepAlive false), one task (batch/task.sh) that is the whole run, and the job set to end when its task does. Batch then
# deletes the pool by itself, whether the task succeeded, failed or ran into the wall-clock cap, and starts the task
# again on a new node when the node is preempted (the task resumes from the checkpoint it synced). Nothing outside Azure
# has to be on, logged in or connected after the launch. The weights, the log and a heartbeat are in the container
# under r2-runs/<Tag>/; fetch-batch.ps1 brings the weights home whenever a machine is next around.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [int]$Seed,
    [Parameter(Mandatory)] [string]$Run,
    [Parameter(Mandatory)] [string]$CacheBlob,
    [string]$ExtraBlob = '',
    [string]$TrainFlags = '',
    # The job is ended, and its pool deleted, after this long whatever the task is doing.
    [int]$MaxHours = 36,
    # Write the job and the task as they would be sent (the SAS left out) and stop before creating anything.
    [switch]$DryRun
)
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $AzConfig.batchAccount) { throw "$AzConfigPath does not name 'batchAccount' (README.md, 'Settings')" }
if (-not (Test-Path (Join-Path $Run 'n2n_smoke.py'))) { throw "$Run holds no n2n_smoke.py: -Run is the frozen run directory" }
$vmDir = Join-Path $RunsDir "$Tag-batch"
if (Test-Path (Join-Path $vmDir 'started.txt')) { throw "$Tag was started already ($vmDir/started.txt); remove it to launch again" }
New-Item -ItemType Directory -Force $vmDir | Out-Null

& $AzCli batch account login -n $AzConfig.batchAccount -g $AzConfig.resourceGroup --subscription $AzConfig.subscription | Out-Null
if ($LASTEXITCODE -ne 0) { throw "could not log in to the Batch account $($AzConfig.batchAccount)" }
$quota = & $AzCli batch account show -n $AzConfig.batchAccount -g $AzConfig.resourceGroup --subscription $AzConfig.subscription --query 'lowPriorityCoreQuota' -o tsv
if ([int]$quota -lt 4) { throw "the Batch account's low-priority quota is $quota cores, a T4 needs 4" }
"Batch account low-priority quota: $quota cores"

& C:\Windows\System32\tar.exe -czf (Join-Path $vmDir 'run.tgz') -C $Run --exclude '__pycache__' --exclude '*.log' --exclude '*.pt' .
if ($LASTEXITCODE -ne 0) { throw 'packing run.tgz failed' }

$image = "$($AzConfig.image)" -split ':'
$job = [ordered]@{
    id = $Tag
    onAllTasksComplete = 'noaction'   # set to terminatejob once the task is in: a job made with it and no task ends at once
    constraints = @{ maxWallClockTime = "PT${MaxHours}H" }
    poolInfo = @{ autoPoolSpecification = [ordered]@{
            autoPoolIdPrefix = $Tag
            poolLifetimeOption = 'job'
            keepAlive = $false
            pool = [ordered]@{
                vmSize = "$($AzConfig.vmSize)".ToLowerInvariant()
                virtualMachineConfiguration = @{
                    imageReference = @{ publisher = $image[0]; offer = $image[1]; sku = $image[2]; version = $image[3] }
                    nodeAgentSKUId = 'batch.node.ubuntu 22.04'
                }
                targetDedicatedNodes = 0
                targetLowPriorityNodes = 1
                taskSlotsPerNode = 1
            }
        }
    }
}
$inBase = Get-ContainerUrl "r2-runs/$Tag/in"
$settings = [ordered]@{ RUN = $Tag; NAME = $Name; SEED = "$Seed"; ACCOUNT = $AzConfig.account; CONTAINER = $AzConfig.container
    CACHE_BLOB = $CacheBlob; EXTRA_BLOB = $ExtraBlob; TRAIN_FLAGS = $TrainFlags; SAS = '<the SAS>' }
function New-Task([string]$sas) {
    $s = [ordered]@{}; foreach ($k in $settings.Keys) { $s[$k] = $settings[$k] }; $s.SAS = $sas
    [ordered]@{
        id = 'train'
        commandLine = "/bin/bash -c 'bash task.sh'"
        resourceFiles = @(@{ httpUrl = "$inBase/task.sh?$sas"; filePath = 'task.sh' }, @{ httpUrl = "$inBase/run.tgz?$sas"; filePath = 'run.tgz' })
        environmentSettings = @($s.Keys | ForEach-Object { @{ name = $_; value = "$($s[$_])" } })
        userIdentity = @{ autoUser = @{ scope = 'pool'; elevationLevel = 'admin' } }
        # A trainer that fails is started again from its checkpoint, three times at most. A preempted node does not count.
        constraints = @{ maxTaskRetryCount = 3; retentionTime = 'P1D' }
    }
}
$job | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $vmDir 'job.json') -Encoding utf8
New-Task '<the SAS>' | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $vmDir 'task.shown.json') -Encoding utf8
if ($DryRun) {
    "dry run: job $Tag would train $Name from $Run on one low-priority $($AzConfig.vmSize), ended after $MaxHours h at the latest"
    "run.tgz: $([math]::Round((Get-Item (Join-Path $vmDir 'run.tgz')).Length / 1KB)) KB; job.json and task.shown.json are in $vmDir"
    Get-Content (Join-Path $vmDir 'task.shown.json')
    return
}

# A SAS that outlives the cap and its retries, in the user-only file (common.ps1); the task carries it as a setting.
"minted a SAS expiring $(New-RunSas ([int][math]::Ceiling($MaxHours / 24.0) + 2))"
$sas = Get-Sas
foreach ($f in (Join-Path $PSScriptRoot 'batch/task.sh'), (Join-Path $vmDir 'run.tgz')) {
    & $AzCopy copy $f "$inBase/$(Split-Path $f -Leaf)?$sas" --overwrite true --output-level essential | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "uploading $f failed" }
}
# The task with its SAS is written beside the SAS itself, user-only, and removed once it is sent.
$secret = Join-Path (Split-Path $AzConfig.sasFile) "$Tag-task.json"
New-Task $sas | ConvertTo-Json -Depth 12 | Set-Content $secret -Encoding utf8
try {
    & $AzCli batch job create --json-file (Join-Path $vmDir 'job.json') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "creating the job $Tag failed" }
    & $AzCli batch task create --job-id $Tag --json-file $secret | Out-Null
    if ($LASTEXITCODE -ne 0) { & $AzCli batch job delete --job-id $Tag --yes | Out-Null; throw "adding the task failed; the job was deleted again" }
    & $AzCli batch job set --job-id $Tag --on-all-tasks-complete terminatejob | Out-Null
    if ($LASTEXITCODE -ne 0) { & $AzCli batch job delete --job-id $Tag --yes | Out-Null; throw 'the job could not be set to end with its task; it was deleted again, since its pool would have stayed' }
}
finally { Remove-Item $secret -Force -ErrorAction SilentlyContinue }
"started $(Get-Date -Format o) as Batch job $Tag" | Out-File (Join-Path $vmDir 'started.txt') -Encoding utf8
"job $Tag is in: its pool is made for it and deleted when it ends (at the latest after $MaxHours h)"
& $AzCli batch job show --job-id $Tag --query '{state:state, onAllTasksComplete:onAllTasksComplete, cap:constraints.maxWallClockTime, pool:executionInfo.poolId}' -o json
"watch: az batch task show --job-id $Tag --task-id train --query '{state:state, node:nodeInfo.nodeId, exit:executionInfo.exitCode, retries:executionInfo.retryCount}'"
"the log, the heartbeat and the weights: $(Get-ContainerUrl "r2-runs/$Tag")"
