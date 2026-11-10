"""Is the starless plate dug (or lifted) under the saturated stars the plate builder subtracted? Per channel,
median over stars of (plate core r<=4 median - plate 19-22 px ring median) / the catalogue's sigma."""
import csv, glob, os, random, sys
import numpy as np
from astropy.io import fits
P = sys.argv[1]
W = 22; yy, xx = np.mgrid[-W:W+1, -W:W+1]; rr = np.hypot(xx, yy)
core = rr <= 4.5; mid = (rr >= 6) & (rr <= 12); far = (rr >= 19) & (rr <= 22)
cats = sorted(glob.glob(f"{P}/*_plate.stars.csv")); random.seed(1); random.shuffle(cats)
allc, allm = [], []
for cp in cats[:40]:
    rows = list(csv.DictReader(open(cp, encoding="utf-8")))
    sat = [r for r in rows if r["saturated"] == "1" and r["outcome"] == "Subtracted"]
    if len(sat) < 5: continue
    fp = cp.replace(".stars.csv", ".fits")
    if not os.path.exists(fp): continue
    d = np.asarray(fits.getdata(fp), dtype=np.float32)
    if d.ndim == 2: continue
    sig = np.nanmedian([float(r["sigma"]) for r in rows])
    random.shuffle(sat); c_, m_ = [], []
    for r in sat[:60]:
        xi, yi = int(round(float(r["x"]))), int(round(float(r["y"])))
        if not (W <= xi < d.shape[2]-W and W <= yi < d.shape[1]-W): continue
        w = d[:, yi-W:yi+W+1, xi-W:xi+W+1]
        if not np.isfinite(w).all(): continue
        f = np.array([np.median(w[c][far]) for c in range(3)])
        c_.append([(np.median(w[c][core]) - f[c]) / sig for c in range(3)])
        m_.append([(np.median(w[c][mid]) - f[c]) / sig for c in range(3)])
    if not c_: continue
    allc += c_; allm += m_
    print(f"{os.path.basename(cp)[:60]:60s} n={len(c_):3d} core {np.round(np.median(c_,0),2)} mid {np.round(np.median(m_,0),2)}")
print(f"ALL n={len(allc)} core RGB {np.round(np.median(allc,0),2)}  mid {np.round(np.median(allm,0),2)}")
