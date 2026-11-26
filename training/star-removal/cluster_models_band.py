"""Route A's check, part two: band.py's statistic on what models leave on the cluster scenes, beside both truths.

    python -I cluster_models_band.py <scene base, old render> <new render dir> <label>=<output fits> ...

Per 300 px cell (the 3 x 3 grid of cluster_truth_band.py), all in the new noisy truth's own noise of a 9 px mean, each
as the median over the eight field cells and the cluster's core (cell 1,1):
1. The band of the old and the new noisy truth and of each output (the outputs ran on the old composite).
2. The band of the DIFFERENCE, each output less the old and the new noisy truth (what it leaves), and the same for the
   oracle, the composite less the stars truth exactly with every noise kept: less the old truth that is the stars' own shot
   noise, which no remover that keeps the noise takes out; less the new truth it is also the faint grain.
3. In the core, what each output leaves against the old truth split by sign: the band of its part below the truth (where
   it took too much) and above (where it left light).
4. A nebula-free scene only (where the smooth light is the new noise-free truth less the sky): what each output leaves
   against the old truth, through the LINEAR band (the 9 px box less the 41 px, no median), fitted as a sum of four
   planes, each scaled by its own kept fraction: the smooth populations' faint stars less their expectation, every other
   star (the stars truth less that: the resolved stars and the compact groups' unresolved ones), the stars' shot noise and
   the smooth light. Printed per plane: the fraction kept and its share of the residual's band power, then the share no
   plane explains, in the core and in the corner field cell 0,0. Reads only.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

B, PAD, EDGE = 300, 48, 62
base = Path(sys.argv[1])
new_dir = Path(sys.argv[2])
outputs = [tuple(a.split("=", 1)) for a in sys.argv[3:]]


def lum(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


def window(img, y0, x0):
    return img[y0 - PAD:y0 + B + PAD, x0 - PAD:x0 + B + PAD]


def band(img, y0, x0, sigma9):
    p7 = median_filter(window(img, y0, x0), 7)
    b = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:PAD + B, PAD:PAD + B]
    return float(np.sqrt(np.mean(b ** 2)) / sigma9)


def linear(img, y0, x0):
    w = window(img, y0, x0)
    v = (uniform_filter(w, 9) - uniform_filter(w, 41))[PAD:PAD + B, PAD:PAD + B].ravel()
    return v - v.mean()


def field_core(values):
    v = np.array(values)
    return f"{np.median(np.delete(v, 4)):5.2f} / {v[4]:5.2f}"


name = base.name
old_noisy, new_noisy = lum(Path(f"{base}_starless_noisy.fits")), lum(new_dir / f"{name}_starless_noisy.fits")
composite, stars = lum(Path(f"{base}.fits")), lum(Path(f"{base}_stars.fits"))
new_clean = lum(new_dir / f"{name}_starless.fits")
cells = [(EDGE + i * B, EDGE + j * B) for i in range(3) for j in range(3)]
s9 = [1.4826 * np.median(np.abs(np.diff(new_noisy[y:y + B, x:x + B], axis=1))) / np.sqrt(2) / 9 for y, x in cells]
results = [(label, lum(path)) for label, path in outputs]
shot = composite - stars - old_noisy

print(f"=== {name}: band, field cells median / the cluster core")
for label, img in [("old truth", old_noisy), ("new truth", new_noisy)] + results:
    print(f"  {label:18s} {field_core([band(img, y, x, s) for (y, x), s in zip(cells, s9)])}")
print("  what it leaves (output less truth), against the old truth / the new truth; in the core below / above the old truth")
for label, img in [("oracle", composite - stars)] + results:
    left_old = [band(img - old_noisy, y, x, s) for (y, x), s in zip(cells, s9)]
    left_new = [band(img - new_noisy, y, x, s) for (y, x), s in zip(cells, s9)]
    (y, x), s = cells[4], s9[4]
    r = img - old_noisy
    print(f"  {label:18s} {field_core(left_old)}   {field_core(left_new)}   "
          f"{band(np.minimum(r, 0), y, x, s):5.2f} / {band(np.maximum(r, 0), y, x, s):5.2f}")

if "neb0" in name:
    faint = lum(Path(f"{base}_unresolved.fits")) - new_clean
    planes = [("faint stars", faint), ("other stars", stars - faint), ("shot noise", shot), ("smooth light", new_clean)]
    print("  against the old truth, linear band: per plane the fraction kept (share of the power); then the share unexplained")
    for label, img in results:
        for where, k in (("core", 4), ("field 0,0", 0)):
            (y, x) = cells[k]
            r = linear(img - old_noisy, y, x)
            X = np.stack([linear(p, y, x) for _, p in planes], axis=1)
            kept, *_ = np.linalg.lstsq(X, r, rcond=None)
            power = np.sum(r ** 2)
            parts = ", ".join(f"{pl} {kv:+.2f} ({np.sum((kv * X[:, i]) ** 2) / power:.2f})" for i, ((pl, _), kv) in enumerate(zip(planes, kept)))
            print(f"  {label:18s} {where:9s} {parts}; unexplained {np.sum((r - X @ kept) ** 2) / power:.2f}")
