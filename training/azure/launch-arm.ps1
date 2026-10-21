# One training arm on an Azure spot T4, launched in one command, under the owner's guards (README.md):
#   pwsh -File launch-arm.ps1 -Tag r2taz -Name r2t_synth4_s1 -Seed 1 -Vm tw-r2t-t4 -Run C:/temp/e2/r2t-run `
#       -CacheBlob r2-synth4/cache -TeacherBlob r2-synth4/teacher-e5 -ExtraBlob r2-clusters/cache `
#       -TrainFlags '--levels 4 --max-ch 256 --clip-grad 8 --schedule cosine --steps 224500' `
#       -FetchTo C:/temp/tianwen-scratch/r2-synth4
# Checks the spot quota, mints a SAS if none is on file (-MintSas mints a fresh one), writes run.env and packs the
# frozen run directory (a copy of training/denoise at a commit, holding n2n_smoke.py) as run.tgz, creates the VM, copies
# every file with retries (the first scp after a create can fail while sshd comes up), starts vm/setup.sh then
# vm/train.sh fully detached (setsid, nohup, no stdin: an ssh that keeps stdin waits for the whole run), registers the
# driver task tw-<tag> (az-driver.ps1), and then WAITS for the trainer and prints its own command line and banner, the
# check that caught R2r's seed 1 running R2q's schedule (2026-10-21). -ResumeFromBlob continues a run whose VM was
# deleted from the resume checkpoint its cron synced (vm/from-blob.sh).
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [int]$Seed,
    [Parameter(Mandatory)] [string]$Vm,
    [Parameter(Mandatory)] [string]$Run,
    [Parameter(Mandatory)] [string]$CacheBlob,
    [Parameter(Mandatory)] [string]$FetchTo,
    [string]$TeacherBlob = '',
    [string]$ExtraBlob = '',
    [string]$ScheduleOn = '',
    [string]$TrainFlags = '',
    # A script the driver runs once the weights are home and the VM is gone, given -Name and -Weights (e.g. a scorer).
    [string]$AfterScript = '',
    [switch]$ResumeFromBlob,
    [switch]$MintSas,
    # Check the quota, write run.env and pack run.tgz, print what would be launched, and stop before creating anything.
    [switch]$DryRun
)
. (Join-Path $PSScriptRoot 'common.ps1')
$vmDir = Join-Path $RunsDir "$Tag-vm"
if (Test-Path (Join-Path $vmDir 'started.txt')) { throw "$Tag was started already ($vmDir/started.txt); remove it to launch again" }
if (-not (Test-Path (Join-Path $Run 'n2n_smoke.py'))) { throw "$Run holds no n2n_smoke.py: -Run is the frozen run directory" }
$sub = $AzConfig.subscription; $rg = $AzConfig.resourceGroup

# Spot quota: the subscription's region quota is shared, and a colleague's training has held 324 of 350 low-priority
# vCPUs.
$usage = & $AzCli vm list-usage --location $AzConfig.location --subscription $sub --query "[?name.value=='lowPriorityCores'].[currentValue, limit]" -o tsv
$used, $limit = "$usage" -split '\s+' | ForEach-Object { [int]$_ }
if ($limit - $used -lt 4) { throw "spot quota: $used of $limit low-priority vCPUs in use, a T4 needs 4" }
"spot quota: $used of $limit in use"

if (-not $DryRun -and ($MintSas -or -not (Test-Path $AzConfig.sasFile))) { "minted a SAS expiring $(New-RunSas)" }

New-Item -ItemType Directory -Force $vmDir | Out-Null
@(
    "RUN=$Tag", "NAME=$Name", "SEED=$Seed", "ACCOUNT=$($AzConfig.account)", "CACHE_BLOB=$CacheBlob", "TEACHER_BLOB=$TeacherBlob",
    "EXTRA_BLOB=$ExtraBlob", "SCHEDULE_ON=$ScheduleOn", "TRAIN_FLAGS=`"$TrainFlags`""
) -join "`n" | Set-Content (Join-Path $vmDir 'run.env') -NoNewline -Encoding ascii
# pwsh's own `tar` is bsdtar, which has no --force-local; Windows' tar.exe takes a C: path as it is.
& C:\Windows\System32\tar.exe -czf (Join-Path $vmDir 'run.tgz') -C $Run --exclude '__pycache__' --exclude '*.log' --exclude '*.pt' .
if ($LASTEXITCODE -ne 0) { throw 'packing run.tgz failed' }
if ($DryRun) {
    "dry run: $Vm ($($AzConfig.vmSize), spot) would train $Name from $Run; run.env:"
    Get-Content (Join-Path $vmDir 'run.env')
    "run.tgz: $([math]::Round((Get-Item (Join-Path $vmDir 'run.tgz')).Length / 1KB)) KB, $((& C:\Windows\System32\tar.exe -tzf (Join-Path $vmDir 'run.tgz')).Count) entries"
    return
}

"creating $Vm"
$ip = & $AzCli vm create -g $rg -n $Vm --subscription $sub --location $AzConfig.location --image $AzConfig.image `
    --size $AzConfig.vmSize --priority Spot --max-price $AzConfig.maxPrice --eviction-policy Deallocate --os-disk-size-gb 128 `
    --storage-sku StandardSSD_LRS --admin-username tw --ssh-key-values "$($AzConfig.sshKey).pub" --public-ip-sku Standard --nsg-rule SSH `
    --tags @($AzConfig.tags) --query publicIpAddress -o tsv 2>$null
if (-not $ip) { throw "no VM: no spot capacity, or the create failed (nothing to clean up unless az resource list shows $Vm)" }
"VM $Vm at $ip"

$k = @('-i', $AzConfig.sshKey, '-o', 'StrictHostKeyChecking=accept-new', '-o', 'ConnectTimeout=60', '-o', 'BatchMode=yes')
$scripts = @('setup.sh', 'train.sh', 'resume.sh', 'from-blob.sh') | ForEach-Object { Join-Path $PSScriptRoot "vm/$_" }
foreach ($copy in @(($scripts + "tw@${ip}:~/"), @((Join-Path $vmDir 'run.tgz'), "tw@${ip}:~/run.tgz"),
        @((Join-Path $vmDir 'run.env'), "tw@${ip}:~/run.env"), @($AzConfig.sasFile, "tw@${ip}:~/.sas"))) {
    $ok = $false
    foreach ($try in 1..6) { & scp @k @copy *> $null; if ($LASTEXITCODE -eq 0) { $ok = $true; break }; Start-Sleep -Seconds 20 }
    if (-not $ok) { throw "copying $($copy[0]) to $Vm failed six times; the VM is up, so delete it (az-driver's Remove-RunVm) or retry" }
}
$chain = if ($ResumeFromBlob) { '~/setup.sh && ~/from-blob.sh && ~/train.sh' } else { '~/setup.sh && ~/train.sh' }
& ssh -n @k "tw@$ip" "chmod 600 ~/.sas; chmod +x ~/*.sh; sed -i 's/\r$//' ~/*.sh ~/run.env; setsid nohup bash -c '$chain' </dev/null >/dev/null 2>&1 & echo started"
if ($LASTEXITCODE -ne 0) { throw "starting the run on $Vm exited $LASTEXITCODE" }
"started $(Get-Date -Format o) at $ip" | Out-File (Join-Path $vmDir 'started.txt') -Encoding utf8

$driver = Join-Path $PSScriptRoot 'az-driver.ps1'
$arguments = "--headless pwsh -NoProfile -File `"$driver`" -Tag $Tag -Name $Name -Vm $Vm -FetchTo `"$FetchTo`""
if ($AfterScript) { $arguments += " -AfterScript `"$AfterScript`"" }
$action = New-ScheduledTaskAction -Execute 'C:\Windows\System32\conhost.exe' -Argument $arguments -WorkingDirectory $RunsDir
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(30) -RepetitionInterval (New-TimeSpan -Minutes 10)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3) -StartWhenAvailable -Priority 4
Register-ScheduledTask -TaskName "tw-$Tag" -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
"driver task tw-$Tag registered, first pass at $((Get-ScheduledTaskInfo -TaskName "tw-$Tag").NextRunTime)"

# The trainer's OWN command line and banner, against what this launch meant to run.
"waiting for the trainer (setup downloads the cache in the VMs' region, then installs torch)..."
foreach ($i in 1..30) {
    Start-Sleep -Seconds 60
    $seen = & ssh -n @k "tw@$ip" "ps -eo args | grep '[n]2n_smoke' | head -1" 2>$null
    if ($seen) {
        Start-Sleep -Seconds 90
        "the trainer runs: $seen"
        & ssh -n @k "tw@$ip" "grep -vE '^\s+(step|val) ' ~/$Tag.log | tail -12; grep -E '^\s+step ' ~/$Tag.log | tail -2"
        return
    }
}
throw "no trainer on $Vm after 30 minutes; read ~/setup.log there (ssh tw@$ip tail ~/setup.log)"
