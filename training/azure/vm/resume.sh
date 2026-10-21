#!/bin/bash
# After a spot eviction (deallocated: the local SSD is wiped, the OS disk keeps ~/cache's resume checkpoint, the venv
# and cron): fetch the blobs run.env names back onto /mnt, then train.sh, which skips the install and resumes from the
# checkpoint. The driver (az-driver.ps1) starts this, detached, when it finds the VM running with no trainer.
set -e
source ~/run.env
exec >> ~/setup.log 2>&1
echo "$(date -Is) resume after eviction"
SAS=$(cat ~/.sas)
BASE="https://${ACCOUNT:?run.env must name ACCOUNT}.blob.core.windows.net/tw-neural-training"
for pair in "$CACHE_BLOB:cache" "$TEACHER_BLOB:teacher" "$EXTRA_BLOB:clusters"; do
    blob=${pair%%:*}; dir=${pair##*:}
    [ -n "$blob" ] || continue
    sudo mkdir -p "/mnt/$dir"; sudo chown "$USER" "/mnt/$dir"
    azcopy copy "$BASE/$blob/*?$SAS" "/mnt/$dir/" --output-level essential
done
ls -la ~/cache/$NAME.resume.pt || true
~/train.sh
