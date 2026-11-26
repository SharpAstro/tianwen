"""Route A's check, part two: band.py's statistic on what models leave on the cluster scenes, beside both truths.

    python -I cluster_models_band.py <scene base, old render> <new render dir> <label>=<output fits> ...

Per 300 px cell (the 3 x 3 grid of cluster_truth_band.py), the band of the old and the new noisy truth and of each output, all in
the new noisy truth's own noise of a 9 px mean; then each output's median over the cells and over the field cells
(the cluster core, cell 1,1, apart). Reads only.
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


def band(img, y0, x0, sigma9):
    ys, xs = slice(y0 - PAD, y0 + B + PAD), slice(x0 - PAD, x0 + B + PAD)
    p7 = median_filter(img[ys, xs], 7)
    b = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:PAD + B, PAD:PAD + B]
    return float(np.sqrt(np.mean(b ** 2)) / sigma9)


name = base.name
images = [("old truth", lum(Path(f"{base}_starless_noisy.fits"))), ("new truth", lum(new_dir / f"{name}_starless_noisy.fits"))]
images += [(label, lum(path)) for label, path in outputs]
ref = images[1][1]
table = {label: [] for label, _ in images}
for i in range(3):
    for j in range(3):
        y0, x0 = EDGE + i * B, EDGE + j * B
        s9 = 1.4826 * np.median(np.abs(np.diff(ref[y0:y0 + B, x0:x0 + B], axis=1))) / np.sqrt(2) / 9
        for label, img in images:
            table[label].append(band(img, y0, x0, s9))
print(f"=== {name}: band, all cells median / field cells median / the cluster core")
for label, values in table.items():
    v = np.array(values)
    field = np.delete(v, 4)
    print(f"  {label:18s} {np.median(v):5.2f} / {np.median(field):5.2f} / {v[4]:5.2f}")
