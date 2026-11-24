# training/azure/

Training runs on an Azure spot T4, beside the desktop's GTX 1070: one command launches an arm, a scheduled task babysits
it, and the weights come home and the VM is deleted without a session watching. Moved here on 2026-10-21 from
`C:/temp/e2` and a session scratchpad, where every run since R2i's seed 1 (2026-10-16) had been driven from (the owner:
"C:/temp/ as the name suggests is not made to last forever").

| file | what |
|---|---|
| `common.ps1` | dot-sourced by the others: the settings file, the runs folder, the SAS helpers |
| `launch-arm.ps1` | one arm in one command: quota check, SAS, `run.env` and `run.tgz`, the VM, the copies, the start, the driver task, then the trainer's own command line |
| `az-driver.ps1` | the driver task `tw-<tag>`: freshness, eviction and resume while training; fetch, load check and DELETE of everything the run made once it is done; then an optional `-AfterScript` (a scorer) |
| `launch-batch.ps1`, `batch/task.sh`, `fetch-batch.ps1` | the same arm as an Azure Batch job that removes itself: no VM of ours, no task on this desktop ("A run that removes itself") |
| `upload-cache.ps1` | a cache folder up to the container, resumable, `-CapMbps` to leave room on the uplink; the cap is live for an upload started with `-CapMbps` over 0 (an uncapped one ignores the file): write a new number of Mb/s into `upload-cap-mbps.txt` in the runs folder and a running upload of a big file follows it within seconds, with nothing restarted; to let go, write a high number such as 1000 (0 or less is ignored, and so is anything that is not a number, with a line in the upload log); a leftover file wins over `-CapMbps` at the start of every later pass, so delete it or overwrite it when done |
| `blockupload.py` | one large file as numbered blocks that a restart continues (Get Block List, then only the missing blocks) |
| `vm/setup.sh`, `vm/train.sh`, `vm/resume.sh`, `vm/from-blob.sh` | on the VM: the downloads, the trainer under nohup with a 10-minute cron sync, the resume after an eviction, and a continue on a new VM from the synced checkpoint |

## Settings

This repository is public, so no script names the subscription, the resource group or the storage account. They come
from a user-only JSON file outside the repository, `$HOME/.azure-upload/tw-training.json` (or the file
`TIANWEN_AZURE_CONFIG` names): `subscription`, `resourceGroup`, `location`, `storageSubscription`,
`storageResourceGroup`, `account`, `container`, `sasFile`, `sshKey`, `vmSize`, `maxPrice`, `image`, `tags`. `batchAccount` is asked for by `launch-batch.ps1` only. A run's status,
log, stop and done files go to the runs folder, `TIANWEN_RUNS` (`C:/temp/e2` by default, where every run so far is
recorded).

## The guards

The owner's, for every run: its own resource group, a spot T4 with a price cap, a budget alert on the group, the data in a
private storage account in the VMs' own region (an account in another region made every cache download inter-region
egress, A$7 of October's A$17), and **everything the run made deleted when it ends, by the driver**, which lists the
group to prove it. The SAS lives only in the user-only file the settings name and in the VM's `~/.sas`, expires in 3 days
(deleting the file does not revoke it), and is never written anywhere else; the container is never public.

## Launching an arm

```
pwsh -File training/azure/launch-arm.ps1 -Tag r2taz -Name r2t_synth4_s1 -Seed 1 -Vm tw-r2t-t4 -Run C:/temp/e2/r2t-run `
    -CacheBlob r2-synth4/cache -TeacherBlob r2-synth4/teacher-e5 -ExtraBlob r2-clusters/cache `
    -TrainFlags '--levels 4 --max-ch 256 --clip-grad 8 --schedule cosine --steps 224500' -FetchTo C:/temp/tianwen-scratch/r2-synth4 `
    -AfterScript training/star-removal/score-arm.ps1
```

`-AfterScript` is the arm's scorer, run by the driver with `-Name` and `-Weights` once the weights are home and the VM is
deleted: `training/star-removal/score-arm.ps1` for a star-remover arm (its paths come from `scorecard.local.json`,
`training/README.md`, "Scoring an arm's weights"). It skips every step whose output exists, so the driver re-running it is
harmless. An arm trained on the desktop's 1070 instead uses `training/star-removal/arm-driver.ps1`.

`-Run` is a frozen copy of `training/denoise` at the commit the arm was registered at. The arm's own flags go in
`-TrainFlags`, after the recipe's, so argparse's last value wins. The launcher ends by printing the trainer's command line
from the VM's process table and the head of its log: **read both against the pre-registration before leaving the run**.
A cache the container does not hold yet goes up first with `upload-cache.ps1`.

**A teacher can be built on the VM instead of uploaded**: `-TeacherMembers 'a.pt,b.pt,c.pt' -MembersBlob <folder>` (in
place of `-TeacherBlob`) has `setup.sh` fetch the members into the cache and `train.sh` run `n2n_teacher.py` over it at 8
views before training. A teacher is 7.6 GB, an hour and a half up a home link against about half an hour on the T4; it
sits on the OS disk, so an eviction keeps it, and the build skips the blocks it has written. The launcher then shows the
teacher starting rather than the trainer, so read the trainer's own command line once the teacher is done.

## A run that removes itself

The owner's rule (2026-11-16): whatever is started removes itself when it is done, with no machine of ours connected
(the VM runs above are deleted by a scheduled task on the desktop, which has to be on and logged in). A VM cannot do
that from inside without a role assignment, which a Contributor cannot make. Azure Batch can, as the service:

```
pwsh -File training/azure/launch-batch.ps1 -Tag r3xaz -Name r3x_synth16_s0 -Seed 0 -Run C:/temp/e2/r3h-run `
    -CacheBlob r2-synth16/cache -TrainFlags '--levels 4 --max-ch 256 --clip-grad 8'
pwsh -File training/azure/fetch-batch.ps1 -Tag r3xaz -Name r3x_synth16_s0 -FetchTo D:/temp/tianwen-scratch/r2-synth16
```

- **The pool exists for the job alone** (`autoPoolSpecification`, `poolLifetimeOption: job`, `keepAlive: false`): one
  low-priority T4 node. The job is set to end when its one task does (`onAllTasksComplete: terminatejob`, set after the
  task is added, since a job made with it and no task ends at once), and Batch then deletes the pool: on success, on a
  failed task, and at the job's wall-clock cap (`-MaxHours`, 36 by default).
- **A preempted node is Batch's to replace**: it starts the task again on a new node, and `batch/task.sh` resumes from
  the checkpoint it synced to the container (the log, the resume checkpoint and a heartbeat go up every 10 minutes; the
  weights at the end, listed back at their size before the task exits 0).
- **The best step's weights come home too when the arm asks for them, on this route only**: `--save-best` in
  `launch-batch.ps1 -TrainFlags` makes the trainer write `<name>.best.pt` beside `<name>.pt` (the weights at the
  held-out objective's best step; with the gate off, as every Azure run has it, `<name>.pt` stays the last step's, as
  every arm before was scored). They travel in the resume checkpoint, the task uploads them before `<name>.pt`, and
  `fetch-batch.ps1` fetches both. The VM route (`launch-arm.ps1`) syncs and fetches `<name>.pt` alone and deletes its
  VM, so it refuses the switch rather than write the file and lose it.
- **What stays, and costs cents or nothing:** the Batch account (`batchAccount` in the settings file), the storage
  account and the job's record. The settings name the Batch account; a low-priority node has no price cap, it bills at
  Batch's low-priority rate.
- **`fetch-batch.ps1` changes nothing in Azure**: it reports the job, the task and any pool still up, and brings the log
  and, once there, the weights home with a load check.
- The task carries the container SAS as a setting. The launcher mints one that outlives the cap, writes the task file
  beside the SAS (user-only) and removes it once sent.

## Traps that each cost a run or an evening

- **The first scp after `az vm create` can fail** while sshd comes up: every copy is checked and retried.
- **An ssh that starts a remote background job must not keep stdin**: `ssh -n` and `setsid nohup ... </dev/null`, or ssh
  waits for the whole run and the driver's next passes are skipped behind it.
- **`pkill -f` and `pgrep -f` read the ssh's own shell**, whose command line is the whole remote command: bracket the
  pattern (`[n]2n_smoke`), and never spell out in that command a name the pattern looks for. The resume check's own
  `~/resume.sh` made it answer 'running' on every pass while an evicted VM sat idle (2026-10-22); it names the script
  through a variable.
- **A cron install line under `set -e` can install an EMPTY crontab** when its grep matches nothing; the sync then stops
  while the trainer runs on. `train.sh` keeps the pipeline whole and checks the line is in.
- **A driver must check the synced log's age**: a dead sync reads as a run in progress for as long as nobody looks.
- **Spot eviction deallocates**: the OS disk keeps the venv and the resume checkpoint, `/mnt` is wiped and refetched.
- **azcopy's default concurrency thrashes a slow uplink** (1,130 retries, 12.7 % of 6.5 GB in 90 minutes on 2026-10-21):
  `upload-cache.ps1` holds it to 8 connections, and a big file goes through `blockupload.py`, which continues where a
  stopped upload left off instead of starting the file again.
- **A connection opened for every block fails often on a home line** (#2300): 4 to 19 % of first tries to the storage
  host timed out whole after 21 s, while the next try from a new port went through, and a failed try was still charged to
  the cap. `blockupload.py` keeps one connection a worker and charges the cap only once a connection stands. One 96 MiB
  file in 1 MiB blocks at a 10 Mb/s cap, 2026-11-21: 23 failed tries and 3.2 Mb/s before; none, four connections and
  9.9 Mb/s after, the blob read back byte for byte. The four calls outside the blocks (head, block list, commit, head)
  still take a connection each, so each is tried up to five times; the same block list committed twice leaves the same
  blob (sent twice to the service, 201 both times). At the defaults (8 MiB blocks, four workers, where a worker's
  connection sits silent for about 20 s between its blocks): 200 MiB, no failed try, four connections for 25 blocks,
  10.0 Mb/s. Not measured: a cap so low that the silence outlasts what the service or a router keeps a connection for
  (about 200 s at 1 Mb/s). A connection lost in that gap costs one charged try, never data: a reset shows at once and
  the next try follows after 5 s, while a connection a router dropped without a word is found only when the send times
  out (the 300 s socket timeout at most).
- **The spot quota is the subscription's, shared**: a colleague's training has held 324 of the region's 350 low-priority
  vCPUs, so the launcher checks for the 4 a T4 needs.
- **pwsh's `tar` is bsdtar** (no `--force-local`); the launcher packs with `C:\Windows\System32\tar.exe`.
