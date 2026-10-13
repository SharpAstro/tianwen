#!/usr/bin/env bash
# R9 (#1056) L-REFINE-T's twins (RULES.md): degrade, stack and sharpen each twin of twins/plan.json; two at once.
T=D:/temp/tianwen-scratch/i1056/bin/tianwen.exe
C=D:/temp/tianwen-scratch/planetary/converted/2022-09-03-1211_1_Red.b4724b4b357e2924.ser
O=D:/Astro-Dataset/planetary/opal
D=D:/temp/tianwen-scratch/i1056/twins
one() {
  local i=$1
  local row=$(python -c "import json; r=json.load(open('$D/plan.json'))[$i]; print(r['map'], r['r0'], r['wind'], r['local'] or 'none', r['scatter'], r['warp'], r['seed'])")
  read map r0 wind local scatter warp seed <<< "$row"
  local n=$(printf "t%03d" $((i + 1))); local d="$D/$n"; mkdir -p "$d"
  [ -f "$d/derived/master_${n}_sharpened.fits" ] && return 0
  local lr=(); [ "$local" != none ] && lr=(--local-r0 "$local")
  "$T" planetary degrade "$C" --map "$O/hlsp_opal_hst_wfc3-uvis_jupiter-${map}_v1_globalmap.fits" --frames 600 --r0 $r0 --wind $wind --exposure-ms 4 "${lr[@]}" \
    --scatter $scatter --warp-rms $warp --seed $seed --real-statistics "$D/red-600.real.json" -o "$d/$n.ser" > "$d/degrade.log" 2>&1 || { echo "$n degrade failed" >> "$D/twins.done"; return; }
  "$T" planetary stack "$d/$n.ser" --truth "$d/$n.truth.fits" -o "$d" > "$d/stack.log" 2>&1
  "$T" planetary sharpen "$d/master_$n.fits" --truth "$d/$n.truth.fits" --planet jupiter --wavelength 650 --telescope newtonian -o "$d/derived" > "$d/sharpen.log" 2>&1
  rm -f "$d/$n.ser"
  echo "$n done" >> "$D/twins.done"
}
export -f one; export T C O D
seq 0 51 | xargs -P 2 -I{} bash -c 'one {}'
echo "all twins" >> "$D/twins.done"
