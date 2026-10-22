#!/bin/bash
# First step of one training run on an Azure spot T4 (training/azure/README.md); ~/run.env names the run. Each blob
# folder onto the local SSD (/mnt, big files), with links in a folder of its own under ~, where the small files and the
# resume checkpoint live on the OS disk, which survives a deallocation: the cache, and the teacher and the extra cache
# only where run.env names them. Then the run directory (run.tgz) into ~/run. train.sh starts the run; resume.sh is this
# download plus train.sh after an eviction.
set -e
source ~/run.env
exec >> ~/setup.log 2>&1
SAS=$(cat ~/.sas)
BASE="https://${ACCOUNT:?run.env must name ACCOUNT}.blob.core.windows.net/tw-neural-training"
fetch() {
    # $1 the blob folder, $2 the folder under /mnt and under ~
    sudo mkdir -p "/mnt/$2"; sudo chown "$USER" "/mnt/$2"; mkdir -p "$HOME/$2"
    echo "$(date -Is) download $1"
    azcopy copy "$BASE/$1/*?$SAS" "/mnt/$2/" --output-level essential
    for f in "/mnt/$2"/*; do ln -sf "$f" "$HOME/$2/$(basename "$f")"; done
    ls -la "$HOME/$2"
}
fetch "$CACHE_BLOB" cache
[ -n "$TEACHER_BLOB" ] && fetch "$TEACHER_BLOB" teacher
[ -n "$EXTRA_BLOB" ] && fetch "$EXTRA_BLOB" clusters
# A teacher built on the VM (train.sh) averages checkpoints that must sit in the cache folder, as real files on the OS disk.
if [ -n "$MEMBERS_BLOB" ]; then
    echo "$(date -Is) download the teacher's members $MEMBERS_BLOB"
    azcopy copy "$BASE/$MEMBERS_BLOB/*?$SAS" "$HOME/cache/" --output-level essential
fi
true
mkdir -p ~/run
tar -xzf ~/run.tgz -C ~/run
echo "$(date -Is) setup done"
