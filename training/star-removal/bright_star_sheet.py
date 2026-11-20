"""One bright star at 1:1 through several removers (the owner's question on HIP 80609, 2026-11-20; #2292).
    python -I bright_star_sheet.py <master fits> <x> <y> <size> <out png> <label>=<starless fits> [...]
The master first, then each starless image, all through the master's own stretch (the runner's rule: per channel the
minimum, and the midtones balance that puts the median at 0.25). Prints per image the ring medians less that frame's sky
(its median at 300 to 400 px) in the master's pixel sigma, per channel, and the first channel's 10th percentile.
A star nearer the frame's edge than 400 px is read on the part of each ring the frame holds."""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image

MASTER = sys.argv[1]
X, Y, SIZE = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
OUT = sys.argv[5]
others = [a.split("=", 1) for a in sys.argv[6:]]
TARGET = 0.25


def planes(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[None] if a.ndim == 2 else a[:3]


m = planes(MASTER)
imgs = [("master", m)] + [(n, planes(p)) for n, p in others]
div = float(np.nanmax(m))
par = []
for c in range(m.shape[0]):
    cov = np.isfinite(m[c]) & (m[c] != 0)
    u = m[c] / div; mn = float(u[cov].min()); med = float(np.median(u[cov] - mn))
    par.append((mn, med * (TARGET - 1.0) / (TARGET * (2.0 * med - 1.0) - med)))
mtf = lambda beta, x: (beta - 1.0) * np.clip(x, 0, 1) / ((2.0 * beta - 1.0) * np.clip(x, 0, 1) - beta)
h = SIZE // 2
sl = (slice(max(0, Y - h), Y + h), slice(max(0, X - h), X + h))   # a crop at the frame's edge is cut, never wrapped


def of_master(values, c):
    """A plate with more channels than the master is read through the master's last channel."""
    return values[min(c, len(values) - 1)]


def show(a):
    out = np.stack([mtf(of_master(par, c)[1], np.clip(np.nan_to_num(a[c][sl], nan=0.0) / div - of_master(par, c)[0], 0, None)) for c in range(a.shape[0])])
    out = np.repeat(out, 3, axis=0) if out.shape[0] == 1 else out
    return np.clip(out, 0, 1).transpose(1, 2, 0)


Image.fromarray((np.concatenate([show(a) for _, a in imgs], axis=1) * 255 + 0.5).astype(np.uint8)).save(OUT)
print(f"{OUT}: " + " / ".join(n for n, _ in imgs) + f", {SIZE} px each")
H, W = m.shape[1:]
yy, xx = np.mgrid[0:H, 0:W]
d = np.hypot(xx - X, yy - Y)
EDGES = [0, 8, 22, 40, 60, 80, 100, 120, 140, 170, 230, 300]
ring = (d >= 300) & (d < 400)
if not ring.any():
    sys.exit(f"no pixel of the frame lies 300 to 400 px from ({X}, {Y}); the sheet is written, the rings are not read")
sig = []
for c in range(m.shape[0]):
    sig.append(1.4826 * float(np.nanmedian(np.abs(np.diff(np.where(ring, m[c], np.nan), axis=1)))) / np.sqrt(2))
for n, a in imgs:
    print(f"{n}: ring medians less its sky, in the master's pixel sigma, R / G / B   | red 10th percentile")
    sky = [float(np.nanmedian(a[c][ring])) for c in range(a.shape[0])]
    for lo, hi in zip(EDGES[:-1], EDGES[1:]):
        sel = (d >= lo) & (d < hi)
        if not sel.any():
            continue
        v = [(float(np.nanmedian(a[c][sel])) - sky[c]) / of_master(sig, c) for c in range(a.shape[0])]
        q = (float(np.nanpercentile(a[0][sel], 10)) - sky[0]) / sig[0]
        print(f"   {lo:3d} to {hi:3d}   " + " / ".join(f"{x:8.2f}" for x in v) + f"   | {q:8.2f}")
