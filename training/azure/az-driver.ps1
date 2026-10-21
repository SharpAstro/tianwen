# The driver of one training run on an Azure spot T4 (launch-arm.ps1 registers it as the task tw-<tag>, conhost
# --headless, priority 4, every 10 minutes, IgnoreNew). A RESUMABLE pass, every call to Azure or the VM bounded so a
# pass always ends:
# - While training: reads the log and the heartbeat the VM's cron syncs to the container, checks the log is FRESH (a
#   dead sync once read as a run in progress for 7 hours), restarts an evicted VM, and resumes a trainer that is not
#   running.
# - Once the log says the model is saved (or the trainer died with a traceback): fetches the weights to -FetchTo, checks
#   they load, and DELETES the VM, its disk and its network pieces at once, then lists the group to prove them gone (the
#   owner, 2026-10-19: deleted when finished, never left for a session to notice).
# - Then runs -AfterScript, if given, with -Name and -Weights (a scorer), and writes <tag>.done.
# Status, log, stop and done files are <tag>-chain.status, <tag>-chain.log, <tag>-chain.stop and <tag>.done in the runs
# folder (common.ps1). Stop it with <tag>-chain.stop, then `schtasks /Change /TN tw-<tag> /DISABLE`.
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string]$Vm,
    [Parameter(Mandatory)] [string]$FetchTo,
    [string]$AfterScript = ''
)
. (Join-Path $PSScriptRoot 'common.ps1')
$sub = $AzConfig.subscription; $rg = $AzConfig.resourceGroup
$status = Join-Path $RunsDir "$Tag-chain.status"
$log = Join-Path $RunsDir "$Tag-chain.log"
$stop = Join-Path $RunsDir "$Tag-chain.stop"
$done = Join-Path $RunsDir "$Tag.done"
$vmDir = Join-Path $RunsDir "$Tag-vm"
$weights = Join-Path $FetchTo "$Name.pt"
function Say([string]$what) { "$(Get-Date -Format o) $what" | Out-File $log -Append -Encoding utf8; "$what $(Get-Date -Format o)" | Out-File $status -Encoding utf8 }
function Halt([string]$why) { Say "FAILED: $why"; New-Item -ItemType File $stop -Force | Out-Null }
# Bounded: a call that has not finished in its time is killed with its tree and reported. On 2026-10-21 a resume's ssh
# stayed open for hours (the remote nohup kept the session's stdin) and IgnoreNew skipped every later pass behind it.
function Invoke-Bounded([string]$file, [string]$arguments, [int]$seconds, [string]$what) {
    $p = Start-Process -FilePath $file -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $RunsDir "$Tag-$what.out.log") -RedirectStandardError (Join-Path $RunsDir "$Tag-$what.err.log")
    if (-not $p.WaitForExit($seconds * 1000)) {
        & taskkill /T /F /PID $p.Id *> $null
        Say "$what took over $seconds s and was stopped"
        return -1
    }
    return $p.ExitCode
}
# One ssh command on the VM: no stdin (-n), keepalives so a dead link ends the session, and a wall-clock bound.
function Invoke-Vm([string]$ip, [string]$command, [int]$seconds, [string]$what) {
    return Invoke-Bounded 'ssh' "-n -o ConnectTimeout=30 -o ServerAliveInterval=15 -o ServerAliveCountMax=4 -o BatchMode=yes -i $($AzConfig.sshKey) tw@$ip `"$command`"" $seconds $what
}
# Syncs, and starts resume.sh only when no trainer and no setup, resume or train script runs, so a second pass never
# starts a second trainer. `pgrep -f` also reads the ssh's own shell, whose command line is this whole string, so nothing
# in it may match the pattern: the pattern is bracketed (it does not match its own text; an unbracketed `pkill -f
# n2n_smoke.py` killed its own session, 2026-10-21), and resume.sh is named through $r, never spelled out (a literal
# ~/resume.sh made every pass answer 'running' while an evicted R2t sat idle, 2026-10-22).
function Resume-Trainer([string]$why) {
    $ip = (& $AzCli vm show -d -g $rg -n $Vm --subscription $sub --query publicIps -o tsv 2>$null)
    if (-not $ip) { Say "$why; no IP for $Vm yet, trying again next pass"; return }
    $remote = "~/sync.sh; if pgrep -f '[n]2n_smoke|[r]esume\.sh|[s]etup\.sh|[t]rain\.sh' >/dev/null; then echo running; else r=~/resume; setsid nohup `$r.sh </dev/null >/dev/null 2>&1 & echo resumed; fi"
    $code = Invoke-Vm $ip $remote 120 'resume'
    $out = Join-Path $RunsDir "$Tag-resume.out.log"
    $said = if (Test-Path $out) { (Get-Content $out -Raw).Trim() } else { '' }
    Say "$why; asked $ip to sync and resume if idle: exit $code, '$said'"
}
# Everything the run made in the group, all named after the VM: the VM, its disk, NIC, IP, NSG and VNET. Proven by a
# listing; a leftover is reported and the next pass retries (a VNET still held by its NIC's deletion is the usual one).
function Remove-RunVm {
    & $AzCli vm delete -g $rg -n $Vm --subscription $sub --yes 2>&1 | Out-Null
    foreach ($d in @(& $AzCli disk list -g $rg --subscription $sub --query "[?starts_with(name, '$Vm')].name" -o tsv 2>$null)) {
        if ($d) { & $AzCli disk delete -g $rg -n $d --subscription $sub --yes 2>&1 | Out-Null }
    }
    & $AzCli network nic delete -g $rg -n "${Vm}VMNic" --subscription $sub 2>&1 | Out-Null
    & $AzCli network public-ip delete -g $rg -n "${Vm}PublicIP" --subscription $sub 2>&1 | Out-Null
    & $AzCli network nsg delete -g $rg -n "${Vm}NSG" --subscription $sub 2>&1 | Out-Null
    & $AzCli network vnet delete -g $rg -n "${Vm}VNET" --subscription $sub 2>&1 | Out-Null
    $left = @(& $AzCli resource list -g $rg --subscription $sub --query "[?starts_with(name, '$Vm')].name" -o tsv 2>$null | Where-Object { $_ })
    if ($left.Count -gt 0) { Say "DELETE INCOMPLETE, left in the group: $($left -join ', ')"; return $false }
    Say "deleted $Vm and its disk and network; nothing named after it is left in the group"
    return $true
}

if (Test-Path $done) { return }
if (Test-Path $stop) { return }
if ($AfterScript -and (Test-Running ([regex]::Escape((Split-Path $AfterScript -Leaf)) + ".*$Name"))) { return }

try {
    if (-not (Test-Path $weights) -or -not (Test-Path (Join-Path $vmDir 'deleted.txt'))) {
        $sas = Get-Sas
        $base = Get-ContainerUrl "r2-runs/$Tag"
        New-Item -ItemType Directory -Force $vmDir | Out-Null
        foreach ($f in "$Tag.log", 'heartbeat.txt') { & $AzCopy copy "$base/$f`?$sas" (Join-Path $vmDir $f) --overwrite true --output-level quiet *> $null }
        $text = if (Test-Path "$vmDir/$Tag.log") { Get-Content "$vmDir/$Tag.log" -Raw } else { '' }
        $beat = if (Test-Path "$vmDir/heartbeat.txt") { (Get-Content "$vmDir/heartbeat.txt" -Raw).Trim() } else { '' }
        $saved = $text -match 'saved -> '
        if (-not $saved -and -not (Test-Path $weights)) {
            $state = (& $AzCli vm get-instance-view -g $rg -n $Vm --subscription $sub --query 'instanceView.statuses[1].code' -o tsv 2>$null)
            if ($state -match 'deallocated|stopped') {
                # Evicted: ask Azure to start it without waiting; a later pass finds it running and resumes the trainer.
                $code = Invoke-Bounded $AzCli "vm start -g $rg -n $Vm --subscription $sub --no-wait" 180 'vm-start'
                Say "the T4 is $state (evicted); start asked (exit $code), the trainer is resumed once it runs"
                return
            }
            if ($state -notmatch 'running') { Say "the T4 is '$state'; waiting for it to run"; return }
            $last = ($text -split "`n" | Where-Object { $_ -match 'step +\d+/' } | Select-Object -Last 1)
            # A trainer that died before saving: the log and the resume checkpoint are in the container already, so the
            # VM goes. A dead heartbeat alone is not that: after an eviction the trainer is down while resume.sh
            # fetches.
            if ($beat -match 'dead' -and $text -cmatch 'Traceback \(most recent call last\)') {
                Say "the trainer DIED before saving (heartbeat '$beat'); last: $("$last".Trim())"
                if (Remove-RunVm) { "deleted $(Get-Date -Format o) after the trainer died" | Out-File "$vmDir/deleted.txt" -Encoding utf8 }
                Halt "the trainer died; see $vmDir/$Tag.log"
                return
            }
            # Freshness: the cron syncs every 10 minutes, so a log older than 30 means the sync died, not the run. Read
            # through azcopy: `az` is a batch file, and cmd splits a SAS at its '&' (R2o read "-1 min old" all run
            # long).
            $listing = & $AzCopy list "$base/$Tag.log?$sas" --properties LastModifiedTime 2>&1
            $age = -1
            $stamp = $listing | Where-Object { "$_" -match 'LastModifiedTime: ' } | Select-Object -First 1
            if ("$stamp" -match 'LastModifiedTime: (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \+0000') {
                $modified = [datetime]::SpecifyKind([datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture), 'Utc')
                $age = [int]((Get-Date).ToUniversalTime() - $modified).TotalMinutes
            }
            if ($beat -match 'dead') { Resume-Trainer "the heartbeat says '$beat' with no traceback"; return }
            if ($age -gt 30 -or $age -lt 0) { Resume-Trainer "STALE: the blob log is $age min old while the T4 is $state"; return }
            Say "$Tag training on the T4 ($state, $beat), log $age min old: $("$last".Trim())"
            return
        }
        if (-not (Test-Path $weights)) {
            New-Item -ItemType Directory -Force $FetchTo | Out-Null
            & $AzCopy copy "$base/$Name.pt`?$sas" "$weights.part" --overwrite true --output-level quiet *> $null
            if (-not (Test-Path "$weights.part")) { Say "the run ended but $Name.pt is not in the container yet"; return }
            # It must load before the VM that made it is deleted.
            $check = & python -c "import torch,sys; ck=torch.load(sys.argv[1], map_location='cpu', weights_only=False); print(len(ck['model']))" "$weights.part" 2>&1
            if ($LASTEXITCODE -ne 0) { Say "the fetched $Name.pt does not load ($check); fetching again next pass"; Remove-Item "$weights.part"; return }
            Move-Item "$weights.part" $weights
            Say "fetched $Name.pt ($check tensors, loads)"
        }
        if (-not (Remove-RunVm)) { return }
        "deleted $(Get-Date -Format o)" | Out-File "$vmDir/deleted.txt" -Encoding utf8
    }
    if ($AfterScript) {
        Say "running $AfterScript -Name $Name -Weights $weights"
        $p = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $AfterScript, '-Name', $Name, '-Weights', $weights -NoNewWindow -PassThru -Wait `
            -RedirectStandardOutput (Join-Path $RunsDir "$Tag-after.log") -RedirectStandardError (Join-Path $RunsDir "$Tag-after.err.log")
        if ($p.ExitCode -ne 0) { Halt "$AfterScript exited $($p.ExitCode); see $Tag-after.err.log"; return }
    }
    "done $(Get-Date -Format o)" | Out-File $done -Encoding utf8
    Say "done: $Name.pt in $FetchTo, the VM deleted$(if ($AfterScript) { ", $(Split-Path $AfterScript -Leaf) run" })"
}
catch {
    Say "FAILED: $_"
    throw
}
