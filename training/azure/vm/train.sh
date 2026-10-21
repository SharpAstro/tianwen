#!/bin/bash
# torch in a venv, then the star remover's recipe (R2i's: training/denoise/run-r2.ps1's command) on seed $SEED under
# nohup, with --teacher and --extra-cache only where run.env names their blobs, --schedule-on where it names one, and
# the arm's own TRAIN_FLAGS last, so argparse's last value wins (R2t: --levels 4 --max-ch 256 --clip-grad 8 --schedule
# cosine --steps 224500). Resumable, with a cron sync of the log, the checkpoints and a heartbeat (is the trainer alive)
# to the container every 10 minutes. Re-running it after an eviction resumes from the checkpoint.
set -e
source ~/run.env
exec >> ~/setup.log 2>&1
if [ ! -x ~/venv/bin/python ] || ! ~/venv/bin/python -c "import torch" 2>/dev/null; then
    # The image lacks python3.10-venv (bit R2o, 2026-10-18).
    sudo apt-get install -y -q python3.10-venv
    rm -rf ~/venv
    python3 -m venv ~/venv
    ~/venv/bin/pip install -q --upgrade pip
    ~/venv/bin/pip install -q torch==2.5.1 --index-url https://download.pytorch.org/whl/cu124
    ~/venv/bin/pip install -q numpy scipy
fi
~/venv/bin/python -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0))"
cat > ~/sync.sh <<'SYNC'
#!/bin/bash
source ~/run.env
SAS=$(cat ~/.sas); BASE="https://${ACCOUNT:?run.env must name ACCOUNT}.blob.core.windows.net/tw-neural-training/r2-runs/$RUN"
if pgrep -f n2n_smoke.py >/dev/null; then echo "$(date -Is) alive" > ~/heartbeat.txt; else echo "$(date -Is) dead" > ~/heartbeat.txt; fi
for f in ~/heartbeat.txt ~/$RUN.log ~/cache/$NAME.resume.pt ~/cache/$NAME.pt; do
    [ -f "$f" ] && azcopy copy "$f" "$BASE/$(basename "$f")?$SAS" --overwrite true --output-level quiet >/dev/null 2>&1
done
exit 0
SYNC
chmod +x ~/sync.sh
# The install line that wiped the crontab on R2i seed 1's resume (grep matching nothing failed the subshell under set
# -e): the || true keeps the pipeline whole, and the check proves the line is in.
{ crontab -l 2>/dev/null | grep -v sync.sh || true; echo '*/10 * * * * /home/tw/sync.sh'; } | crontab -
crontab -l | grep -q sync.sh || { echo "cron sync NOT installed"; exit 1; }
cd ~/run
# One array, used below: an edit that built it and left a second, hard-coded command running ran R2r's seed 1 as R2q's
# schedule until a check of the trainer's own command line caught it (2026-10-21).
EXTRA_ARGS=()
[ -n "$TEACHER_BLOB" ] && EXTRA_ARGS+=(--teacher /home/tw/teacher)
[ -n "$EXTRA_BLOB" ] && EXTRA_ARGS+=(--extra-cache /home/tw/clusters)
[ -n "$SCHEDULE_ON" ] && EXTRA_ARGS+=(--schedule-on "$SCHEDULE_ON")
[ -n "$TRAIN_FLAGS" ] && EXTRA_ARGS+=($TRAIN_FLAGS)
echo "$(date -Is) train $NAME (seed $SEED)"
nohup ~/venv/bin/python n2n_smoke.py --train --cache /home/tw/cache --synthetic --loss l2 --upsample --cond-map --band-loss 3 \
    --band-scales "2,4 4,8" --base 32 --schedule plateau --steps 480000 --val-every 500 --patience 24 --max-decays 4 \
    --min-improve 0.001 --gate-every 0 --seed $SEED --out $NAME.pt --checkpoint-every 2000 --resume \
    "${EXTRA_ARGS[@]}" >> ~/$RUN.log 2>&1 &
echo $! > ~/train.pid
echo "$(date -Is) started pid $(cat ~/train.pid)"
