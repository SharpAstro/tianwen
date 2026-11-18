"""The grain a starless image holds between 9 and 41 px, per 300 px cell (R4c's and R4d's reported row, 2026-11-19).

    python -I band.py <store> <session stem> <cell i,j> ... -- <label>=<starless fits> ...

Per image and channel: the image through a 7 px median, then its 9 px box mean less its 41 px box mean, the rms over
the cell, in units of the noise of a 9 px mean (the truth plate's neighbour-difference sigma in the cell over 9).
The truth plate is always read first. A cell is 300 px, i down and j across, as crowd_texture.py numbers them.
"""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

store, stem = sys.argv[1], sys.argv[2]
k = sys.argv.index("--")
cells = [tuple(int(v) for v in c.split(",")) for c in sys.argv[3:k]]
images = [("truth plate", f"{store}/starless/plates/{stem}_plate.fits")] + [tuple(a.split("=", 1)) for a in sys.argv[k + 1:]]
B, pad = 300, 48
plate = np.asarray(fits.getdata(images[0][1]), np.float32)
print("cell   image            9 to 41 px band, in sigma of a 9 px mean, R / G / B")
for i, j in cells:
    y0, x0 = i * B, j * B
    ys, xs = slice(max(0, y0 - pad), y0 + B + pad), slice(max(0, x0 - pad), x0 + B + pad)
    inner = (slice(y0 - ys.start, y0 - ys.start + B), slice(x0 - xs.start, x0 - xs.start + B))
    sig = [1.4826 * np.median(np.abs(np.diff(plate[c, y0:y0 + B, x0:x0 + B], axis=1))) / np.sqrt(2) for c in range(3)]
    for label, path in images:
        a = np.asarray(fits.getdata(path), np.float32)
        out = []
        for c in range(3):
            p7 = median_filter(a[c, ys, xs], 7)
            band = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[inner]
            scale = np.median(plate[c, y0:y0 + B, x0:x0 + B]) / np.median(a[c, y0:y0 + B, x0:x0 + B])
            out.append(np.sqrt(np.mean(band ** 2)) * (scale if abs(scale - 1) > 0.02 else 1.0) / (sig[c] / 9))
        print(f"{i},{j}    {label:16s} " + " / ".join(f"{v:.1f}" for v in out), flush=True)
