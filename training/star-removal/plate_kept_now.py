"""Sources a rebuilt plate keeps that the stored plate had subtracted: where they are and what they are (2026-11-23).

    python -I plate_kept_now.py <master.fits> <stored plate.fits> <stored stars.csv> <rebuilt plate.fits>
                                <rebuilt stars.csv> <fwhm px> <out.png>

A plain finder on both plates (the luminance smoothed by the PSF at `<fwhm px>`, which is the luminance width, the
LAST element of the stats' `FwhmPx` (the builder's lumPsf.Fwhm); the plan's 388 and 33 on Centaurus A are at 2.111, and
the count against starless-1867 is on a knife edge: 31 at 2.1, 32 at 2.1113827, 35 at 2.12; less the same smoothed six times wider; local maxima
over 5 of the filtered plate's own robust sigma). A source of the rebuilt plate with none within 1.5 px on the stored
plate, and no star the rebuilt build subtracted within 3 px, is "kept now". For each: the nearest star of the stored
list and of the rebuilt list with its outcome, and the source's place in the frame. The sheet shows the three 128 px
cells holding the most of them: master, stored plate, rebuilt plate, at 3x by pixel repetition on one stretch.
"""
import csv
import sys
from collections import Counter
import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter
from scipy.spatial import cKDTree
from PIL import Image


def lum_of(path):
    data = fits.getdata(path).astype(np.float64)
    return data.mean(axis=0) if data.ndim == 3 else data


def stars_of(path):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    return (np.array([float(r["x"]) for r in rows]), np.array([float(r["y"]) for r in rows]),
            np.array([r["outcome"] for r in rows]), np.array([float(r["significance"]) for r in rows]),
            np.array([r["second_pass"] in ("1", "True", "true") for r in rows]),
            np.array([float(r["width"]) for r in rows]))


def find(lum, s):
    present = np.isfinite(lum) & (lum != 0)
    filled = np.where(present, lum, np.nanmedian(lum[present]))
    f = gaussian_filter(filled, s) - gaussian_filter(filled, 6 * s)
    v = f[present]
    noise = 1.4826 * np.median(np.abs(v - np.median(v)))
    py, px = np.nonzero((f == maximum_filter(f, size=5)) & (f > 5 * noise) & present)
    return f, noise, px, py


master, old_plate, old_csv, new_plate, new_csv, fwhm, out_png = sys.argv[1:8]
s = float(fwhm) / 2.355
lm, lo, ln = lum_of(master), lum_of(old_plate), lum_of(new_plate)
xo, yo, oo, so, po, wo = stars_of(old_csv)
xn, yn, on, sn, pn, wn = stars_of(new_csv)
fo, no, pxo, pyo = find(lo, s)
fn, nn, pxn, pyn = find(ln, s)
new_only = cKDTree(np.c_[pxo, pyo]).query(np.c_[pxn, pyn])[0] > 1.5
subn = on == "Subtracted"
far = cKDTree(np.c_[xn[subn], yn[subn]]).query(np.c_[pxn, pyn])[0] > 3.0
kept = new_only & far
qx, qy = pxn[kept], pyn[kept]
h, w = ln.shape
print(f"kept now: {kept.sum():,} sources; rebuilt value median {np.median(fn[qy, qx] / nn):.1f} sigma, stored value there median {np.median(fo[qy, qx] / no):+.1f}")
do, jo = cKDTree(np.c_[xo, yo]).query(np.c_[qx, qy])
dn, jn = cKDTree(np.c_[xn, yn]).query(np.c_[qx, qy])
near_o = do <= 2.0
near_n = dn <= 2.0
print(f"  a star of the stored list within 2 px: {near_o.sum():,} ({Counter(oo[jo[near_o]]).most_common(4)}; second pass {po[jo[near_o]].mean() * 100:.0f} %, "
      f"median significance {np.median(so[jo[near_o]]) if near_o.any() else float('nan'):.1f}, median width {np.median(wo[jo[near_o]]) if near_o.any() else float('nan'):.2f})")
print(f"  a star of the rebuilt list within 2 px: {near_n.sum():,} ({Counter(on[jn[near_n]]).most_common(4)})")
r = np.hypot(qx - w / 2, qy - h / 2) / np.hypot(w / 2, h / 2)
print(f"  place: {np.mean(r < 1 / 3) * 100:.0f} % in the inner third of the half diagonal (9 % of the frame's area would hold 11 % at random), "
      f"median distance from the centre {np.median(r):.2f}")
cell = 128
hist, ye, xe = np.histogram2d(qy, qx, bins=[np.arange(0, h + 1, cell), np.arange(0, w + 1, cell)])
print(f"  128 px cells holding any: {(hist > 0).sum()} of {hist.size}; the fullest three hold {np.sort(hist.ravel())[-3:][::-1].astype(int).tolist()}")
pres_lo = np.isfinite(lo) & (lo != 0)
med_all = np.median(lo[pres_lo])
sig_all = 1.4826 * np.median(np.abs(lo[pres_lo] - med_all))
rows = []
for _ in range(3):
    iy, ix = np.unravel_index(np.argmax(hist), hist.shape)
    n_here = int(hist[iy, ix])
    hist[iy, ix] = 0
    y0, x0 = int(ye[iy]), int(xe[ix])
    sl = (slice(y0, y0 + cell), slice(x0, x0 + cell))
    cell_ok = pres_lo[sl]
    med = np.median(lo[sl][cell_ok])
    sd = 1.4826 * np.median(np.abs(lo[sl][cell_ok] - med))
    lo_v, hi_v = med - 6 * sd, med + 20 * sd
    panels = [np.clip((img[sl] - lo_v) / (hi_v - lo_v), 0, 1) for img in (lm, lo, ln)]
    gap = np.ones((cell, 2))
    rows.append(np.concatenate([p if i == 0 else np.concatenate([gap, p], axis=1) for i, p in enumerate(panels)], axis=1))
    print(f"  look crop at x {x0}, y {y0}: {n_here} kept-now sources; the cell's level over the frame's median, in the frame's spread: {(med - med_all) / sig_all:+.1f}")
sheet = np.concatenate([r_ if i == 0 else np.concatenate([np.ones((2, r_.shape[1])), r_], axis=0) for i, r_ in enumerate(rows)], axis=0)
sheet = np.repeat(np.repeat(sheet, 3, axis=0), 3, axis=1)
Image.fromarray((sheet * 255).astype(np.uint8)).save(out_png)
print(f"  wrote {out_png} (columns: master, stored plate, rebuilt plate)")
