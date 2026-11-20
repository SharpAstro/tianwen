"""The crowded-gap diagnostic at 1:1 (2026-11-20): the input, our plate, our plate with the judge's broad take, the judge.

    python -I judge_broad_look.py <master fits> <ours> <ours + judge's broad> <judge> <out png> [size, default 400] [x y]

The third plate is judge_broad.py's. Without x y the crop is the <size> px box where the judge's broad take beyond ours is
largest (green, a box's width away from the frame's edge). The four panels go through the master's own stretch (the
runner's rule), so a level difference shows as one. Prints the crop's centre and, for our plate and for the diagnostic
plate, the crop's median level less the judge's in pixel sigma. That sigma is the crop's own: the MAD of horizontal
neighbour differences of the MASTER's green channel inside the crop, so it is neither judge_broad.py's per-plate figure
nor the per-pixel rms map purity_parts.py divides by.
"""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image
from scipy.ndimage import uniform_filter

MASTER, OURS, BROAD, JUDGE, OUT = sys.argv[1:6]
SIZE = int(sys.argv[6]) if len(sys.argv) > 6 else 400
TARGET = 0.25


def planes(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[None] if a.ndim == 2 else a[:3]


m, o, b, j = planes(MASTER), planes(OURS), planes(BROAD), planes(JUDGE)
g = min(1, m.shape[0] - 1)
if len(sys.argv) > 8:
    x, y = int(sys.argv[7]), int(sys.argv[8])
else:
    if min(m.shape[1:]) <= 2 * SIZE:
        sys.exit(f"the frame is {m.shape[2]} x {m.shape[1]}: no {SIZE} px box lies a box's width from every edge; give a smaller size or x y")
    take = uniform_filter(np.nan_to_num(o[g] - b[g], nan=0.0), SIZE)
    take[:SIZE] = take[-SIZE:] = -np.inf; take[:, :SIZE] = take[:, -SIZE:] = -np.inf
    y, x = np.unravel_index(int(np.argmax(take)), take.shape)
print(f"crop centre x {x} y {y}")
div = float(np.nanmax(m))
par = []
for c in range(m.shape[0]):
    cov = np.isfinite(m[c]) & (m[c] != 0)
    u = m[c] / div; mn = float(u[cov].min()); med = float(np.median(u[cov] - mn))
    par.append((mn, med * (TARGET - 1.0) / (TARGET * (2.0 * med - 1.0) - med)))
h = SIZE // 2
sl = (slice(max(0, y - h), y + h), slice(max(0, x - h), x + h))


def mtf(beta, v):
    v = np.clip(v, 0, 1)
    return (beta - 1.0) * v / ((2.0 * beta - 1.0) * v - beta)


def show(a):
    out = np.stack([mtf(par[c][1], np.clip(np.nan_to_num(a[c][sl], nan=0.0) / div - par[c][0], 0, None)) for c in range(a.shape[0])])
    out = np.repeat(out, 3, axis=0) if out.shape[0] == 1 else out
    return np.clip(out, 0, 1).transpose(1, 2, 0)


Image.fromarray((np.concatenate([show(a) for a in (m, o, b, j)], axis=1) * 255 + 0.5).astype(np.uint8)).save(OUT)
sig = 1.4826 * np.nanmedian(np.abs(np.diff(m[g][sl], axis=1))) / np.sqrt(2)
print(f"{OUT}: input / ours / ours with the judge's broad take / judge; in this crop the median level less the judge's, in pixel sigma: "
      f"ours {np.nanmedian((o[g] - j[g])[sl]) / sig:+.2f}, with the broad take {np.nanmedian((b[g] - j[g])[sl]) / sig:+.2f}")
