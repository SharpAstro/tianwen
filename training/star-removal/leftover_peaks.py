"""Point sources the truth plate still holds in a crowded cell, against the photon noise (2026-11-18).
    python -I leftover_peaks.py <session stem>
Luminance of the plate less its 21 px median, through a Gaussian at the PSF's width; the filtered noise is taken from a
sparse cell's filtered spread scaled by the two cells' pixel noise (neighbour differences). Local maxima per 300 px cell at
5, 4 and 3 sigma, beside the stars the builder catalogued there, and the same count on the master."""
import csv, os, sys, json
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, median_filter, maximum_filter
STORE = os.environ.get("TIANWEN_SR_STORE", "D:/Astro-Dataset/2026-10-12-full"); B = 300; stem = sys.argv[1]
P = f"{STORE}/starless/plates/{stem}_plate"
plate = np.asarray(fits.getdata(P + ".fits"), np.float32).mean(0)
master = np.asarray(fits.getdata(f"{STORE}/session-masters/{stem}.fits"), np.float32).mean(0)
stars = list(csv.DictReader(open(P + ".stars.csv", encoding="utf-8")))
prof = json.load(open(P + ".profile.json", encoding="utf-8")); print("profile keys:", list(prof)[:12])
x = np.array([float(s["x"]) for s in stars]); y = np.array([float(s["y"]) for s in stars]); sg = np.array([float(s["significance"]) for s in stars])
H, W = plate.shape
h, _, _ = np.histogram2d(y, x, bins=[H // B, W // B], range=[[0, H // B * B], [0, W // B * B]])
fw = float(np.median([float(s["width"]) for s in stars])); print("median catalogued width", fw)
FS = 1.2                                      # filter sigma in px, about a 2.8 px FWHM
def pixnoise(a): return 1.4826 * np.median(np.abs(np.diff(a, axis=1))) / np.sqrt(2)
def cell(a, i, j): return a[i * B:(i + 1) * B, j * B:(j + 1) * B]
finite = [np.unravel_index(k, h.shape) for k in np.argsort(h, axis=None) if np.isfinite(cell(plate, *np.unravel_index(k, h.shape))).all()]
si, sj = finite[len(finite) // 10]
def filt(a): return gaussian_filter(a - median_filter(a, 21), FS)
sp = cell(plate, si, sj); fsp = filt(sp); ratio = 1.4826 * np.median(np.abs(fsp - np.median(fsp))) / pixnoise(sp)
print(f"sparse cell {si},{sj}: filtered noise over pixel noise {ratio:.3f}")
print("cell    catalogued (of them under 10 sigma)   peaks left in the PLATE at 5 / 4 / 3 sigma    peaks in the MASTER at 5 sigma   plate filtered spread over photon noise")
cells = [np.unravel_index(k, h.shape) for k in np.argsort(-h, axis=None)[:5]] + [(si, sj)]
for i, j in cells:
    p = cell(plate, i, j); m = cell(master, i, j)
    if not np.isfinite(p).all(): continue
    n = pixnoise(p) * ratio; fp = filt(p) / n; fm = filt(np.nan_to_num(m)) / n
    pk = lambda f, t: int(((f == maximum_filter(f, 5)) & (f >= t)).sum())
    inc = (x >= j * B) & (x < (j + 1) * B) & (y >= i * B) & (y < (i + 1) * B)
    print(f"{i},{j:<4d} {int(inc.sum()):6d} ({int((sg[inc] < 10).sum()):4d})                      {pk(fp, 5):5d} / {pk(fp, 4):5d} / {pk(fp, 3):5d}                 {pk(fm, 5):6d}                    {1.4826 * np.median(np.abs(fp - np.median(fp))):.2f}")

# Where the plate's 5 sigma leftovers sit against the builder's own catalogue.
from scipy.spatial import cKDTree
oc = np.array([s["outcome"] for s in stars]); t_sub = cKDTree(np.c_[x[oc == "Subtracted"], y[oc == "Subtracted"]]); t_oth = cKDTree(np.c_[x[oc != "Subtracted"], y[oc != "Subtracted"]])
print("cell    leftovers at 5 sigma: within 2 px of a subtracted star / within 3 px of a candidate left in / 2 to 5 px from a subtracted star / further than 5 px from any; median significance of the last class")
for i, j in cells:
    p = cell(plate, i, j)
    if not np.isfinite(p).all(): continue
    fp = filt(p) / (pixnoise(p) * ratio); yy, xx = np.nonzero((fp == maximum_filter(fp, 5)) & (fp >= 5))
    pts = np.c_[xx + j * B, yy + i * B]; d1 = t_sub.query(pts)[0]; d2 = t_oth.query(pts)[0]; z = fp[yy, xx]
    a = d1 <= 2; b = ~a & (d2 <= 3); c = ~a & ~b & (d1 <= 5); d = ~a & ~b & ~c
    print(f"{i},{j:<4d} {len(z):4d}: {a.sum():4d} / {b.sum():4d} / {c.sum():4d} / {d.sum():4d}      {np.median(z[d]) if d.any() else float('nan'):.1f}   (all: median {np.median(z):.1f}, p90 {np.percentile(z, 90):.1f})")
