#!/bin/bash
# A run continued on a NEW VM (launch-arm.ps1 -ResumeFromBlob), run between setup.sh and train.sh: brings back what the
# old VM's cron synced to the container, the resume checkpoint into ~/cache (a real file on the OS disk, where
# n2n_smoke.py --resume looks) and the log, so train.sh resumes at the checkpoint's step and appends to the run's own
# log. First used 2026-10-21 for R2r's seed 1, whose first VM was deleted at step 45,800; the resumed run read the same
# held-out value at step 44,500 as the first VM had, so a resume on a new VM is exact.
set -e
source ~/run.env
exec >> ~/setup.log 2>&1
SAS=$(cat ~/.sas)
BASE="https://${ACCOUNT:?run.env must name ACCOUNT}.blob.core.windows.net/tw-neural-training/r2-runs/$RUN"
echo "$(date -Is) fetch $NAME.resume.pt and $RUN.log from the container"
azcopy copy "$BASE/$NAME.resume.pt?$SAS" ~/cache/$NAME.resume.pt --output-level essential
azcopy copy "$BASE/$RUN.log?$SAS" ~/$RUN.log --output-level essential
ls -la ~/cache/$NAME.resume.pt ~/$RUN.log
[ -s ~/cache/$NAME.resume.pt ] || { echo "no resume checkpoint came back for $NAME"; exit 1; }
echo "$(date -Is) resumed on a new VM from the container's resume checkpoint" >> ~/$RUN.log
