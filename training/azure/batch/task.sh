#!/bin/bash
# One training run as an Azure Batch task (launch-batch.ps1, README.md "A run that removes itself"). Batch starts this on
# a low-priority T4 node of a pool made for the job, starts it again on a new node when the node is preempted, and deletes
# the pool when the task ends, however it ends: no machine outside Azure has to be reachable for any of that. So this
# script is the whole run, in the foreground, and everything it must keep goes to the container as it goes:
#   - the cache comes from the container (the same region); a resume checkpoint and the log come back when a former
#     node synced them, so a preempted run continues at its last checkpoint and appends to its own log;
#   - the trainer runs under this shell, with the log, the resume checkpoint and a heartbeat synced every 10 minutes;
#   - at the end the weights go up, are listed back at their size, and only then the task exits 0.
# Environment (the task's settings): RUN, NAME, SEED, ACCOUNT, CONTAINER, CACHE_BLOB, EXTRA_BLOB (optional), TRAIN_FLAGS, SAS.
set -uo pipefail
W="${AZ_BATCH_TASK_WORKING_DIR:?run by Azure Batch}"
cd "$W"
BASE="https://${ACCOUNT:?}.blob.core.windows.net/${CONTAINER:?}"
RUNBASE="$BASE/r2-runs/${RUN:?}"
LOG="$W/$RUN.log"
CACHE="$W/cache"
say() { echo "$(date -Is) $*"; }
put() { [ -f "$1" ] && azcopy copy "$1" "$RUNBASE/$(basename "$1")?$SAS" --overwrite true --output-level quiet >/dev/null 2>&1; }
in_container() { azcopy list "$RUNBASE/$1?$SAS" --machine-readable 2>/dev/null | grep -q "Content Length"; }

# A task started again after its weights went up (a preemption between the last sync and the exit) has nothing to do.
if in_container "$NAME.pt"; then say "$NAME.pt is in the container already; nothing to train"; exit 0; fi

# torch in a venv on the node's shared folder, which a retry on the same node finds again. The image lacks
# python3.10-venv (bit R2o, 2026-10-18).
V="${AZ_BATCH_NODE_SHARED_DIR:?}/venv"
if [ ! -x "$V/bin/python" ] || ! "$V/bin/python" -c "import torch" 2>/dev/null; then
    say "installing torch"
    sudo apt-get install -y -q python3.10-venv >/dev/null || { say "apt-get failed"; exit 1; }
    rm -rf "$V"
    python3 -m venv "$V" && "$V/bin/pip" install -q --upgrade pip \
        && "$V/bin/pip" install -q torch==2.5.1 --index-url https://download.pytorch.org/whl/cu124 \
        && "$V/bin/pip" install -q numpy scipy || { say "the venv could not be built"; exit 1; }
fi
"$V/bin/python" -c "import torch, sys; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NO GPU'); sys.exit(0 if torch.cuda.is_available() else 1)" \
    || { say "torch sees no GPU on this node; not training on a CPU"; exit 1; }

mkdir -p "$CACHE"
say "download $CACHE_BLOB"
azcopy copy "$BASE/$CACHE_BLOB/*?$SAS" "$CACHE/" --output-level essential || { say "the cache did not come down"; exit 1; }
EXTRA_ARGS=()
if [ -n "${EXTRA_BLOB:-}" ]; then
    mkdir -p "$W/clusters"
    say "download $EXTRA_BLOB"
    azcopy copy "$BASE/$EXTRA_BLOB/*?$SAS" "$W/clusters/" --output-level essential || { say "the extra cache did not come down"; exit 1; }
    EXTRA_ARGS+=(--extra-cache "$W/clusters")
fi
ls -la "$CACHE"

# What a former node synced: the resume checkpoint beside the cache, where --resume looks, and the log to append to.
if in_container "$NAME.resume.pt"; then
    azcopy copy "$RUNBASE/$NAME.resume.pt?$SAS" "$CACHE/$NAME.resume.pt" --output-level essential
    in_container "$RUN.log" && azcopy copy "$RUNBASE/$RUN.log?$SAS" "$LOG" --output-level essential
    [ -s "$CACHE/$NAME.resume.pt" ] || { say "the resume checkpoint did not come back"; exit 1; }
    echo "$(date -Is) resumed on a new node from the container's resume checkpoint" >> "$LOG"
    say "resuming from the container's checkpoint"
fi

mkdir -p "$W/run" && tar -xzf "$W/run.tgz" -C "$W/run" || { say "run.tgz did not unpack"; exit 1; }
cd "$W/run"
# The arm's own TRAIN_FLAGS go last, so argparse's last value wins, as vm/train.sh has it. One array, one command.
# shellcheck disable=SC2206
[ -n "${TRAIN_FLAGS:-}" ] && EXTRA_ARGS+=($TRAIN_FLAGS)
CMD=("$V/bin/python" n2n_smoke.py --train --cache "$CACHE" --synthetic --loss l2 --upsample --cond-map --band-loss 3
    --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 480000 --val-every 500 --patience 24 --max-decays 4
    --min-improve 0.001 --gate-every 0 --seed "$SEED" --out "$NAME.pt" --checkpoint-every 2000 --resume "${EXTRA_ARGS[@]}")
say "train $NAME (seed $SEED): ${CMD[*]}"
echo "$(date -Is) command: ${CMD[*]}" >> "$LOG"
"${CMD[@]}" >> "$LOG" 2>&1 &
PID=$!

sync_all() {
    echo "$(date -Is) $1" > "$W/heartbeat.txt"
    put "$W/heartbeat.txt"; put "$LOG"; put "$CACHE/$NAME.resume.pt"
}
# The first sync early, so the trainer's own command line and banner can be read from outside within minutes.
sleep 180; sync_all alive
while kill -0 "$PID" 2>/dev/null; do
    for _ in $(seq 60); do kill -0 "$PID" 2>/dev/null || break; sleep 10; done
    sync_all alive
done
wait "$PID"; CODE=$?
say "the trainer exited $CODE"
sync_all "ended $CODE"
# Upload one of the run's outputs and read its size back from the container.
put_checked() {
    local file size
    file=$(basename "$1")
    put "$1"
    size=$(stat -c %s "$1")
    azcopy list "$RUNBASE/$file?$SAS" --machine-readable 2>/dev/null | grep -q "Content Length: $size" \
        || { say "$file is not in the container at $size bytes"; return 1; }
    say "$file is in the container at $size bytes"
}
if [ "$CODE" -eq 0 ] && [ -f "$CACHE/$NAME.pt" ]; then
    # The best step's weights (--save-best in TRAIN_FLAGS) go up FIRST. $NAME.pt in the container is what says the
    # run is done (the check at the top), so a node lost between the two uploads must not leave a finished run
    # without them: it trains to the end again from the resume checkpoint, which carries them.
    if [ -f "$CACHE/$NAME.best.pt" ]; then put_checked "$CACHE/$NAME.best.pt" || exit 1; fi
    put_checked "$CACHE/$NAME.pt" || exit 1
    exit 0
fi
say "no weights: the trainer left no $NAME.pt"
exit 1
