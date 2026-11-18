#!/bin/bash
# Ring profiles at real sites for each model named, its outputs in a folder of its own: site_remove.py runs the remover
# (as trained), site_core.py and site_core_ch.py read the rings. Run from this folder: bash site-run.sh <model> ...
# Paths are DE1's unless TIANWEN_SR_MODELS (the folder of score-<model>/<model>.onnx), TIANWEN_SR_SITE, TIANWEN_DATASET
# and TIANWEN_CLI say otherwise.
M42="ZWO-ASI294MC_UV-IR-Cut_Great-Orion-Nebula_2021-11-28_ZWO ASI294MC_Orion M42 RGB 4min_UV_IR Cut"
HIP="ZWO-ASI533MC-Pro_Optolong-L-Quad-Enhance_HIP-80609_2026-04-21_ZWO ASI533MC Pro_HIP 80609_Optolong L-Quad Enhance"
for m in "$@"; do
  export SITE_OUT=${TIANWEN_SR_SITE:-D:/temp/tianwen-scratch/site}/$m; mkdir -p $SITE_OUT
  python -I site_remove.py ${TIANWEN_SR_MODELS:-C:/temp/tianwen-scratch/f2-logs-master}/score-$m/$m.onnx "$M42" "$HIP" > $SITE_OUT/remove.txt 2>&1 || { echo "remove failed $m"; tail -5 $SITE_OUT/remove.txt; continue; }
  echo "== $m"; python -I site_core.py "$M42" ZWO-ASI294MC "$HIP" ZWO-ASI533MC-Pro | tee $SITE_OUT/site.txt
  echo "-- $m M42 blue"; python -I site_core_ch.py "$M42" ZWO-ASI294MC 2 | tee $SITE_OUT/site-m42-blue.txt
done
