#!/usr/bin/env bash
# L-GAINS-S step A, third round (RULES.md): a warp the stack's mesh cannot follow. p10 and p11 a fast local warp (2 and 3 px RMS,
# lag-1 0.1), p12 a turbulent layer at altitude (r0 15 cm at 10 km, 20 m/s) in place of the warp. Each degraded, stacked, sharpened.
T=${T:-D:/temp/tianwen-scratch/i1056/bin/tianwen.exe}
EDGE="D:/Astro-Dataset/planetary/Jupiter-1100 EdgeHD + ASI678MC-f14-DS/2023-11-11-0244_2-MPO.ser"
O=D:/Astro-Dataset/planetary/opal
D=D:/temp/tianwen-scratch/i1056/deploy
probe() {
  local i=$1
  local row=$(python -c "import json; r=json.load(open('$D/probes.json'))[$i]; print(r['name'], r['r0'], r['wind'], r['local'], r['scatter'], ','.join(map(str, r['defocus'])), r['frames'], r['seed'], r['warp'], r.get('lag1', 0.9), r.get('high', 0), r.get('exposure', 0), r.get('length', 20), r.get('blur', 0))")
  read n r0 wind local scatter defocus frames seed warp lag1 high exposure length blur <<< "$row"
  local d="$D/probes/$n"; mkdir -p "$d"
  [ -f "$d/derived/master_${n}_sharpened.fits" ] && return 0
  local layer=(--warp-rms $warp --warp-lag1 $lag1 --warp-length $length)
  [ "$blur" != "0" ] && layer+=(--static-blur $blur)
  [ "$high" != "0" ] && layer=(--high-r0 $high --high-altitude 10 --high-wind 20)
  local m="$O/hlsp_opal_hst_wfc3-uvis_jupiter-2024c"
  "$T" planetary degrade "$EDGE" --map "${m}_f631n_v1_globalmap.fits" --bayer-maps "${m}_f631n_v1_globalmap.fits,${m}_f502n_v1_globalmap.fits,${m}_f467m_v1_globalmap.fits" \
    --bayer-k 0.999,0.950,0.950 --bayer-defocus-nm $defocus --aperture-mm 280 --obstruction 0.375 --exposure-ms $exposure --r0 $r0 --wind $wind --outer-scale 4 \
    --local-r0 $local --local-outer-scale 0.12 --local-wind 1 --scatter $scatter --scatter-core 5 "${layer[@]}" --frames $frames \
    --real-statistics "$D/edgehd-600.statistics.json" --seed $seed -o "$d/$n.ser" > "$d/degrade.log" 2>&1 || { echo "$n degrade failed" >> "$D/probes.done"; return; }
  "$T" planetary stack "$d/$n.ser" --truth "$d/$n.truth.fits" -o "$d" > "$d/stack.log" 2>&1
  "$T" planetary sharpen "$d/master_$n.fits" --truth "$d/$n.truth.fits" --planet jupiter --wavelength 610,535,460 --aperture-mm 280 --obstruction 0.375 \
    -o "$d/derived" > "$d/sharpen.log" 2>&1
  rm -f "$d/$n.ser"
  echo "$n done" >> "$D/probes.done"
}
export -f probe; export T EDGE O D
for i in ${PROBES:-9 10 11}; do echo $i; done | xargs -P 3 -I{} bash -c 'probe {}'
echo "probes ${PROBES:-9 10 11}" >> "$D/probes.done"
