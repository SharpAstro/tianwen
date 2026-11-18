"""What the plate builder's threshold finds and costs (#2203, 2026-11-18).
    python -I threshold_cmp.py <stem> <label=plates dir> [<label=plates dir> ...]      (the first is the reference)
Per plate: the builder's outcomes; then per 300 px cell class (the five densest by the reference's catalogue, the five
brightest by the plate's level, one sparse cell): stars catalogued, local maxima left at 5 / 4 / 3 sigma of the photon
noise, the luminance's spread after a 3 px box less its 21 px median (white noise about 1), and the spread of this
plate less the reference's (what the lower threshold took beyond the reference)."""
import csv, sys, collections
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, median_filter, maximum_filter, uniform_filter
B = 300; stem = sys.argv[1]; arms = [a.split("=", 1) for a in sys.argv[2:]]
def load(d):
    P = f"{d}/{stem}_plate"; pl = np.asarray(fits.getdata(P + ".fits"), np.float32).mean(0)
    st = list(csv.DictReader(open(P + ".stars.csv", encoding="utf-8"))); return pl, st
data = {k: load(d) for k, d in arms}; ref = arms[0][0]; rp, rs = data[ref]
H, W = rp.shape
def cell(a, i, j): return a[i * B:(i + 1) * B, j * B:(j + 1) * B]
def pixnoise(a): return 1.4826 * np.median(np.abs(np.diff(a, axis=1))) / np.sqrt(2)
def filt(a): return gaussian_filter(a - median_filter(a, 21), 1.2)
x = np.array([float(s["x"]) for s in rs]); y = np.array([float(s["y"]) for s in rs])
h, _, _ = np.histogram2d(y, x, bins=[H // B, W // B], range=[[0, H // B * B], [0, W // B * B]])
ok = [(i, j) for i in range(H // B) for j in range(W // B) if all(np.isfinite(cell(data[k][0], i, j)).all() for k, _ in arms)]
dense = sorted(ok, key=lambda c: -h[c])[:5]; sparse = sorted(ok, key=lambda c: h[c])[len(ok) // 10]
bright = sorted(ok, key=lambda c: -np.median(cell(rp, *c)))[:5]
sp = cell(rp, *sparse); fsp = filt(sp); ratio = 1.4826 * np.median(np.abs(fsp - np.median(fsp))) / pixnoise(sp)
print(stem[:70])
for k, _ in arms: print(f"  {k:10s} outcomes {dict(collections.Counter(s['outcome'] for s in data[k][1]))}")
for name, cs in (("densest five cells", dense), ("brightest five cells", bright), ("a sparse cell", [sparse])):
    print(f"  {name} {cs}")
    print(f"    {'plate':10s} {'catalogued':>10s} {'subtracted':>10s}   left at 5 / 4 / 3 sigma      structure   plate less reference, spread (3 px box, sigma)")
    for k, _ in arms:
        pl, st = data[k]; xs = np.array([float(s["x"]) for s in st]); ys = np.array([float(s["y"]) for s in st]); sub = np.array([s["outcome"] == "Subtracted" for s in st])
        n = ns = 0; pk = [0, 0, 0]; stv = []; dv = []
        for i, j in cs:
            p = cell(pl, i, j); nz = pixnoise(cell(rp, i, j)); f = filt(p) / (nz * ratio)
            inc = (xs >= j * B) & (xs < (j + 1) * B) & (ys >= i * B) & (ys < (i + 1) * B); n += inc.sum(); ns += (inc & sub).sum()
            for t, th in enumerate((5, 4, 3)): pk[t] += int(((f == maximum_filter(f, 5)) & (f >= th)).sum())
            stv.append(np.sqrt((((uniform_filter(p, 3) - median_filter(p, 21)) / (nz / 3)) ** 2).mean()))
            dv.append(np.std(uniform_filter(p - cell(rp, i, j), 3)) / (nz / 3))
        print(f"    {k:10s} {n:10d} {ns:10d}   {pk[0]:6d} / {pk[1]:6d} / {pk[2]:6d}      {np.mean(stv):6.2f}      {np.mean(dv):6.2f}")
