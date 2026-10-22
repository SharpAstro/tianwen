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
| `upload-cache.ps1` | a cache folder up to the container, resumable, `-CapMbps` to leave room on the uplink |
| `blockupload.py` | one large file as numbered blocks that a restart continues (Get Block List, then only the missing blocks) |
| `vm/setup.sh`, `vm/train.sh`, `vm/resume.sh`, `vm/from-blob.sh` | on the VM: the downloads, the trainer under nohup with a 10-minute cron sync, the resume after an eviction, and a continue on a new VM from the synced checkpoint |

## Settings

This repository is public, so no script names the subscription, the resource group or the storage account. They come
from a user-only JSON file outside the repository, `$HOME/.azure-upload/tw-training.json` (or the file
`TIANWEN_AZURE_CONFIG` names): `subscription`, `resourceGroup`, `location`, `storageSubscription`,
`storageResourceGroup`, `account`, `container`, `sasFile`, `sshKey`, `vmSize`, `maxPrice`, `image`, `tags`. A run's status,
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
    -TrainFlags '--levels 4 --max-ch 256 --clip-grad 8 --schedule cosine --steps 224500' -FetchTo C:/temp/tianwen-scratch/r2-synth4
```

`-Run` is a frozen copy of `training/denoise` at the commit the arm was registered at. The arm's own flags go in
`-TrainFlags`, after the recipe's, so argparse's last value wins. The launcher ends by printing the trainer's command line
from the VM's process table and the head of its log: **read both against the pre-registration before leaving the run**.
A cache the container does not hold yet goes up first with `upload-cache.ps1`.

**A teacher can be built on the VM instead of uploaded**: `-TeacherMembers 'a.pt,b.pt,c.pt' -MembersBlob <folder>` (in
place of `-TeacherBlob`) has `setup.sh` fetch the members into the cache and `train.sh` run `n2n_teacher.py` over it at 8
views before training. A teacher is 7.6 GB, an hour and a half up a home link against about half an hour on the T4; it
sits on the OS disk, so an eviction keeps it, and the build skips the blocks it has written. The launcher then shows the
teacher starting rather than the trainer, so read the trainer's own command line once the teacher is done.

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
- **The spot quota is the subscription's, shared**: a colleague's training has held 324 of the region's 350 low-priority
  vCPUs, so the launcher checks for the 4 a T4 needs.
- **pwsh's `tar` is bsdtar** (no `--force-local`); the launcher packs with `C:\Windows\System32\tar.exe`.
