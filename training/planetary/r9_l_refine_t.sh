#!/usr/bin/env bash
# R9 (#1056) L-REFINE-T (RULES.md): once every twin is made, gather them (training without the R8 twins' map, 2022b f631n), fine-tune
# L-REFINE's net on them, then refine and score every held-out reading: the 2024d twins, the six f631n twins, the three R8 twins (G2).
# Progress in lt.done.
set -u
I=D:/temp/tianwen-scratch/i1056
R=C:/Users/SebastianGodelet/source/repos/sharpastro/tianwen-r9/training/planetary
T="D:\\temp\\tianwen-scratch\\i1056\\bin\\tianwen.exe"
PSF=D:/temp/tianwen-scratch/planetary/synthetic/r8m/nostill.psf
S=D:/temp/tianwen-scratch/planetary/synthetic
cd $I
until grep -q "all twins" twins/twins.done 2>/dev/null; do sleep 60; done
rm -rf twinpairs twinheld twinleak
python $R/r9_twin_pairs.py --twins twins --plan twins/plan.json --out twinpairs --exclude-map 2022b_f631n --extra t000 >> lt.done
python $R/r9_twin_pairs.py --twins twins --plan twins/plan.json --out twinheld --held >> lt.done
python $R/r9_twin_pairs.py --twins twins --plan twins/plan.json --out twinleak --only-map 2022b_f631n >> lt.done
python $R/r9_refiner.py train --pairs twinpairs --held twinheld --init model/model.pt --steps 4000 --every 500 --val 4 --out model-t > train-t.log 2>&1
echo "trained $?" >> lt.done
# The R8 twins of G2 in the pair layout (their masters and derived sharpenings as G2 made them, their truths), so one scorer reads all.
rm -rf r8pairs; mkdir -p r8pairs; echo "index,map,psf,when,radius_px,north_deg,keep,broaden_px,finest_snr,noise_sigma,size" > r8pairs/pairs.csv
k=0
for twin in r8/calibrated r8/nostill r8w/warped; do
  name=$(echo $twin | tr / _); b=$(basename $twin); n=$(printf "%05d" $k)
  cp g2/$name/master_$b.fits r8pairs/${n}_stack.fits; cp g2/$name/derived/master_${b}_sharpened.fits r8pairs/${n}_sharp.fits
  cp $S/$twin.truth.fits r8pairs/${n}_truth.fits; echo "$k,$name,,,,,,,,,1" >> r8pairs/pairs.csv; k=$((k + 1))
done
# Every held-out reading through the fine-tuned net, and through L-REFINE's for the comparison.
for model in model-t model; do
  for set in twinheld twinleak r8pairs; do
    out=$set-refined-$model
    rm -rf $out
    python $R/r9_refiner.py apply --model $model/model.pt --pairs $set --out $out > /dev/null 2>&1
    python $R/r9_score.py --tianwen "$T" --pairs $set --refined $out --psf $PSF --out $out-scores.csv > $out.txt 2>&1
    echo "$set through $model scored $?" >> lt.done
  done
done
echo "all done" >> lt.done
