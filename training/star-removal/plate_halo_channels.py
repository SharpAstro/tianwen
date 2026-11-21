"""The plate round a bright star per channel, against the master at the same radius (the owner's question, 2026-11-20).
    python -I plate_halo_channels.py <session master fits> <plate fits> <x> <y>
Per channel and ring: the master's and the plate's median less that channel's sky (its median in the 300 to 400 px ring),
in the channel's pixel sigma, and the plate's 10th percentile over the ring (a hole shows there first)."""
import sys
import numpy as np
from astropy.io import fits

MASTER, PLATE = sys.argv[1:3]
X, Y = float(sys.argv[3]), float(sys.argv[4])
m = np.asarray(fits.getdata(MASTER), np.float32); m = m[None] if m.ndim == 2 else m
p = np.asarray(fits.getdata(PLATE), np.float32); p = p[None] if p.ndim == 2 else p
H, W = p.shape[1:]
yy, xx = np.mgrid[0:H, 0:W]
d = np.hypot(xx - X, yy - Y)
EDGES = [0, 8, 22, 40, 60, 80, 100, 120, 140, 170, 230, 300]
for c in range(p.shape[0]):
    ok = np.isfinite(p[c]) & np.isfinite(m[c])
    ring = (d >= 300) & (d < 400) & ok
    sp, sm = float(np.median(p[c][ring])), float(np.median(m[c][ring]))
    far = p[c][ring.any(axis=1)][:, ring.any(axis=0)]
    sig = 1.4826 * float(np.nanmedian(np.abs(np.diff(np.where(ring, p[c], np.nan), axis=1)))) / np.sqrt(2)
    print(f"channel {c}: sky {sp:.5g}, pixel sigma {sig:.4g}")
    print("   radius px     master   plate median   plate 10th percentile   (less the sky, in pixel sigma)")
    for lo, hi in zip(EDGES[:-1], EDGES[1:]):
        sel = (d >= lo) & (d < hi) & ok
        if sel.any():
            v = p[c][sel]
            print(f"   {lo:3d} to {hi:3d}   {float(np.median(m[c][sel]) - sm) / sig:9.1f}   {float(np.median(v) - sp) / sig:9.2f}   {float(np.percentile(v, 10) - sp) / sig:9.2f}")
