#!/bin/bash
# Ring profiles at real sites for the two sessions site-run.sh leaves out: Lobster and the SMC (2026-11-18, #2207).
LOB="QHYCCD-QHY294C_IDAS-LPS-D3_Lobster-Nebula_2026-08-01_QHY294PROC_Lobster Nebula_IDAS LPS D3"
SMC="ZWO-ASI585MC-Pro_Optolong-L-eNhance_Small-Magellanic-Cloud_2024-09-27_ZWO ASI585MC Pro_SMC 120s LEnh ASI585 252g_Optolong L-eNhance"
for m in "$@"; do
  export SITE_OUT=D:/temp/tianwen-scratch/site/$m; mkdir -p $SITE_OUT
  python -I site_remove.py C:/temp/tianwen-scratch/f2-logs-master/score-$m/$m.onnx "$LOB" "$SMC" > $SITE_OUT/remove-rest.txt 2>&1 || { echo "remove failed $m"; tail -5 $SITE_OUT/remove-rest.txt; continue; }
  echo "== $m"; python -I site_core_o.py "$LOB" QHYCCD-QHY294C "$SMC" ZWO-ASI585MC-Pro 2>/dev/null | tee $SITE_OUT/site-rest.txt
done
