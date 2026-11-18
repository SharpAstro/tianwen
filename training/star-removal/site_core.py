"""Rings round catalogued stars, mean of the three planes, real site output of transplant.py; prints the star's own light too.

    python -I site_core.py <session stem> <tag> [<session stem> <tag> ...]

tag is the one transplant.py prints for the stem.
"""
import os, csv, sys, glob, numpy as np
from astropy.io import fits
STORE=os.environ.get("TIANWEN_DATASET","D:/Astro-Dataset/2026-10-12-full"); OUT=os.environ.get("TIANWEN_TRANSPLANT","D:/temp/tianwen-scratch/transplant")
yy,xx=np.mgrid[-20:21,-20:21]; rr=np.hypot(yy,xx); sky=(rr>=12)&(rr<20)
E=[0,0.8,1.6,2.5,3.5,4.5,6,8]
for stem,tag in zip(sys.argv[1::2],sys.argv[2::2]):
    rd=lambda p: np.nanmean(np.asarray(fits.getdata(p),dtype=np.float32),0)
    plate=rd(f"{STORE}/starless/plates/{stem}_plate.fits"); master=rd(f"{STORE}/session-masters/{stem}.fits")
    out=rd(glob.glob(f"{OUT}/{tag}*{os.environ.get('TIANWEN_SR_MODEL', '')}*-real-out.fits")[0]); H,W=plate.shape; print(stem[:40]); print("   radii",E)
    for lo,hi in ((100,1000),(1000,1e9)):
      for inp in ("0","1"):
        acc={k:[] for k in ("layer","plate","out")}
        for s in csv.DictReader(open(f"{STORE}/starless/plates/{stem}_plate.stars.csv",encoding="utf-8")):
            sg=float(s["significance"])
            if not(lo<=sg<hi) or s["saturated"]=="1" or s["second_pass"]!="0" or s["inpainted"]!=inp: continue
            x,y=int(round(float(s["x"]))),int(round(float(s["y"])))
            if not(80<=x<W-80 and 80<=y<H-80): continue
            sig=float(s["sigma"])/3**.5; sl=np.s_[y-20:y+21,x-20:x+21]
            if not np.isfinite(master[sl]).all(): continue
            for k,im in (("layer",master[sl]-plate[sl]),("plate",plate[sl]-np.median(plate[sl][sky])),("out",out[sl]-np.median(out[sl][sky]))):
                acc[k].append([im[(rr>=a)&(rr<b)].mean()/sig for a,b in zip(E,E[1:])])
        for k in acc:
            if acc[k]: print(f"  {lo}+ inpainted={inp} {k:5s} n={len(acc[k]):5d} "+" ".join(f"{v:8.2f}" for v in np.median(np.array(acc[k]),0)))
