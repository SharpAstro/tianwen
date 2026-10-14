#!/usr/bin/env bash
# L-GAINS-S's blurred extension (RULES.md, amended 2026-10-14): colour twins of the EdgeHD 11 Jupiter of 2023-11-11 over the deploy
# family's air, each with a static blur (`planetary degrade --static-blur`, the bin-s build) drawn from 1.0 to 4.5 px; degrade, stack,
# sharpen. plan-f.json holds them (f000 on). Progress in extend-f.done; a twin is claimed by the first maker to start it. Stop file:
# extend-f.stop.
T=D:/temp/tianwen-scratch/i1056/bin-s/tianwen.exe
EDGE="D:/Astro-Dataset/planetary/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO.ser"
O=D:/Astro-Dataset/planetary/opal
D=D:/temp/tianwen-scratch/i1056/deploy
one() {
  [ -f "$D/extend-f.stop" ] && return 0
  local i=$1
  local row=$(python -c "import json; r=json.load(open('$D/plan-f.json'))[$i]; print(r['epoch'], r['r0'], r['wind'], r['local'], r['scatter'], ','.join(map(str, r['defocus'])), r['blur'], r['seed'])")
  read epoch r0 wind local scatter defocus blur seed <<< "$row"
  local n=$(printf "f%03d" $i); local d="$D/$n"; mkdir -p "$d"
  [ -f "$d/derived/master_${n}_sharpened.fits" ] && return 0
  mkdir "$D/claims" 2>/dev/null; mkdir "$D/claims/$n" 2>/dev/null || return 0  # one maker per twin, however many run
  local m="$O/hlsp_opal_hst_wfc3-uvis_jupiter-${epoch}"
  "$T" planetary degrade "$EDGE" --map "${m}_f631n_v1_globalmap.fits" --bayer-maps "${m}_f631n_v1_globalmap.fits,${m}_f502n_v1_globalmap.fits,${m}_f467m_v1_globalmap.fits" \
    --bayer-k 0.999,0.950,0.950 --bayer-defocus-nm $defocus --aperture-mm 280 --obstruction 0.375 --exposure-ms 0 --r0 $r0 --wind $wind --outer-scale 4 \
    --local-r0 $local --local-outer-scale 0.12 --local-wind 1 --scatter $scatter --scatter-core 5 --static-blur $blur --frames 600 \
    --real-statistics "$D/edgehd-600.statistics.json" --seed $seed -o "$d/$n.ser" > "$d/degrade.log" 2>&1 || { echo "$n degrade failed" >> "$D/extend-f.done"; return; }
  "$T" planetary stack "$d/$n.ser" --truth "$d/$n.truth.fits" -o "$d" > "$d/stack.log" 2>&1
  "$T" planetary sharpen "$d/master_$n.fits" --truth "$d/$n.truth.fits" --planet jupiter --wavelength 610,535,460 --aperture-mm 280 --obstruction 0.375 \
    -o "$d/derived" > "$d/sharpen.log" 2>&1
  rm -f "$d/$n.ser"
  echo "$n done" >> "$D/extend-f.done"
}
export -f one; export T EDGE O D
count=$(python -c "import json; print(len(json.load(open('$D/plan-f.json'))))")
seq 0 $((count - 1)) | xargs -P ${1:-1} -I{} bash -c 'one {}'
echo "all blurred twins" >> "$D/extend-f.done"
