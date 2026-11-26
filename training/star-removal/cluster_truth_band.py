"""Route A's check: band.py's statistic (through a 7 px median, the 9 px box mean less the 41 px one, rms over a 300 px
cell, in units of the noise of a 9 px mean) on the cluster scenes' starless truths rendered both ways.

    python -I cluster_truth_band.py <dir old> <dir new>

For every scene in both dirs (matched by name), on the channels' mean: first the composites, the noise-free one (the
starless truth plus the stars truth) old against new as its largest difference, and the noisy one ({name}.fits) as its
rms difference in the new noisy truth's noise over the scene; then per 300 px cell of the 1024 px scene (a 3 x 3 grid
62 px in from the edge) the band of the noisy starless truth (the training target) old and new, the band of the old less
the new noise-free truth (the faint grain alone), and the band of the new noise-free truth (the smooth light and the
nebula's texture). The noise is the noisy truth's own neighbour-difference sigma in the cell. Reads only.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

B, PAD, EDGE = 300, 48, 62
old_dir, new_dir = Path(sys.argv[1]), Path(sys.argv[2])


def lum(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


def band(img, y0, x0, sigma9):
    ys, xs = slice(y0 - PAD, y0 + B + PAD), slice(x0 - PAD, x0 + B + PAD)
    p7 = median_filter(img[ys, xs], 7)
    b = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:PAD + B, PAD:PAD + B]
    return float(np.sqrt(np.mean(b ** 2)) / sigma9)


for noisy_old in sorted(old_dir.glob("*_starless_noisy.fits")):
    name = noisy_old.name.replace("_starless_noisy.fits", "")
    noisy_new = new_dir / noisy_old.name
    if not noisy_new.exists():
        print(f"{name}: no new render", file=sys.stderr)
        continue
    no, nn = lum(noisy_old), lum(noisy_new)
    so, sn = lum(old_dir / f"{name}_starless.fits"), lum(new_dir / f"{name}_starless.fits")
    to, tn = lum(old_dir / f"{name}_stars.fits"), lum(new_dir / f"{name}_stars.fits")
    comp_o, comp_n = lum(old_dir / f"{name}.fits"), lum(new_dir / f"{name}.fits")
    sig_scene = 1.4826 * np.median(np.abs(np.diff(nn, axis=1))) / np.sqrt(2)
    print(f"=== {name}: noise-free composite old less new, largest {np.max(np.abs((so + to) - (sn + tn))):.1e} (its peak "
          f"{np.max(sn + tn):.2f}); noisy composite old less new, rms {np.sqrt(np.mean((comp_o - comp_n) ** 2)) / sig_scene:.4f} noise")
    print("cell  noisy truth old / new  grain alone (old less new)  new truth noise-free")
    rows = []
    for i in range(3):
        for j in range(3):
            y0, x0 = EDGE + i * B, EDGE + j * B
            sig = 1.4826 * np.median(np.abs(np.diff(nn[y0:y0 + B, x0:x0 + B], axis=1))) / np.sqrt(2)
            s9 = sig / 9
            r = (band(no, y0, x0, s9), band(nn, y0, x0, s9), band(so - sn, y0, x0, s9), band(sn, y0, x0, s9))
            rows.append(r)
            print(f"{i},{j}   {r[0]:5.2f} / {r[1]:5.2f}            {r[2]:5.2f}                      {r[3]:5.2f}")
    m = np.median(np.array(rows), axis=0)
    print(f"median {m[0]:5.2f} / {m[1]:5.2f}            {m[2]:5.2f}                      {m[3]:5.2f}")
