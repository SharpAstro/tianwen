"""Where a plate ADDS light over the input on the nebula (nebula_rows.py's own definition), at 1:1 (2026-11-20).
    python -I nebula_added_look.py <master> <base starless> <plate> <out png> <x> <y> [size 500]
Panels: input / base starless / the plate / the plate with its added pixels on the nebula in red and the base plate's
own added pixels in yellow (the nebula mask's edge is drawn in blue). All through the master's stretch.
Prints the share of the nebula each plate adds light on (the row's figure without its border rule, so a little apart
from nebula_rows.py's), and how dark the input is at the pixels the plate adds and the base does not: the input less
its own 31 px median there, in pixel sigma. A fill of real dark structure reads several sigma under zero."""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image
from scipy.ndimage import binary_erosion, median_filter, uniform_filter

MASTER, BASE, PLATE, OUT = sys.argv[1:5]
X, Y = int(sys.argv[5]), int(sys.argv[6]); SIZE = int(sys.argv[7]) if len(sys.argv) > 7 else 500
BLOCK, FILTER, NEBULA_SIGMA = 8, 7, 20.0


def planes(p):
    a = np.asarray(fits.getdata(p), np.float32)
    return a[None] if a.ndim == 2 else a[:3]


m, b, p = planes(MASTER), planes(BASE), planes(PLATE)
inp = np.nan_to_num(m.mean(axis=0)); h, w = inp.shape
sig = 1.4826 * float(np.median(np.abs(np.diff(inp[::4], axis=1)))) / np.sqrt(2)
bh, bw = h // BLOCK, w // BLOCK
blocks = np.median(inp[:bh * BLOCK, :bw * BLOCK].reshape(bh, BLOCK, bw, BLOCK).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
small = median_filter(blocks, FILTER)
big = np.zeros_like(inp); big[:bh * BLOCK, :bw * BLOCK] = np.repeat(np.repeat(small, BLOCK, 0), BLOCK, 1)
neb = big - float(np.percentile(small, 5)) > NEBULA_SIGMA * sig
over_p = (uniform_filter(np.nan_to_num(p.mean(axis=0)) - inp, 3) > sig) & neb
over_b = (uniform_filter(np.nan_to_num(b.mean(axis=0)) - inp, 3) > sig) & neb
print(f"nebula {int(neb.sum())} px; added on it: the plate {100 * over_p[neb].mean():.2f} %, the base {100 * over_b[neb].mean():.2f} %; "
      f"of the plate's added pixels {100 * (over_p & over_b).sum() / max(1, over_p.sum()):.0f} % are the base's too")
# How dark the added pixels are in the input against their surroundings: the input less its own 31 px median, in sigma.
dip = (inp - median_filter(inp[::2, ::2], 15).repeat(2, 0).repeat(2, 1)[:h, :w]) / sig
new = over_p & ~over_b
print(f"the input at the newly added pixels, less its own 31 px median, in pixel sigma: median {np.median(dip[new]):+.2f}, "
      f"10th to 90th percentile {np.percentile(dip[new], 10):+.2f} to {np.percentile(dip[new], 90):+.2f}; over the whole nebula: median {np.median(dip[neb]):+.2f}")
div = float(np.nanmax(m)); par = []
for c in range(m.shape[0]):
    cov = np.isfinite(m[c]) & (m[c] != 0)
    u = m[c] / div; mn = float(u[cov].min()); med = float(np.median(u[cov] - mn))
    par.append((mn, med * (0.25 - 1.0) / (0.25 * (2.0 * med - 1.0) - med)))
hh = SIZE // 2; sl = (slice(Y - hh, Y + hh), slice(X - hh, X + hh))


def show(a):
    def mtf(beta, v):
        v = np.clip(v, 0, 1); return (beta - 1.0) * v / ((2.0 * beta - 1.0) * v - beta)
    o = np.stack([mtf(par[c][1], np.clip(np.nan_to_num(a[c][sl]) / div - par[c][0], 0, None)) for c in range(a.shape[0])])
    o = np.repeat(o, 3, axis=0) if o.shape[0] == 1 else o
    return np.clip(o, 0, 1).transpose(1, 2, 0)


marked = show(p).copy()
edge = neb[sl] & ~binary_erosion(neb[sl])
marked[edge] = (0.2, 0.4, 1.0); marked[over_b[sl]] = (1.0, 0.9, 0.1); marked[(over_p & ~over_b)[sl]] = (1.0, 0.1, 0.1)
Image.fromarray((np.concatenate([show(m), show(b), show(p), marked], axis=1) * 255 + 0.5).astype(np.uint8)).save(OUT)
print(OUT)
