"""How much of M31's nucleus a star remover takes as a star: on the owner's Andromeda master, the share
of the input's light, per channel, that a starless frame no longer holds within 6 px of the nucleus (the brightest point
of the 41 px box-smoothed luminance, 2071, 1487) and from 6 to 12 px. SXT and StarNet2 take about none; E6 took 16.5 /
7.9 / 6.2 % (R / G / B) on 2026-10-18. Glue.

    python m31_nucleus.py <input master.fits> <starless.fits> [...] [--x 2071 --y 1487]
"""
import argparse

import numpy as np
from astropy.io import fits

ap = argparse.ArgumentParser()
ap.add_argument('master')
ap.add_argument('starless', nargs='+')
ap.add_argument('--x', type=int, default=2071)
ap.add_argument('--y', type=int, default=1487)
args = ap.parse_args()
inp = fits.getdata(args.master).astype(np.float64)
x, y = args.x, args.y
yy, xx = np.mgrid[:inp.shape[1], :inp.shape[2]]
R = np.hypot(xx - x, yy - y)
for path in args.starless:
    st = inp - fits.getdata(path).astype(np.float64)
    parts = []
    for lo, hi in ((0, 6), (6, 12)):
        m = (R >= lo) & (R < hi)
        parts.append(" / ".join(f"{100 * np.nanmedian(st[c][m]) / np.nanmedian(inp[c][m]):+5.1f}%" for c in range(3)))
    print(f"{path}: nucleus 0-6 px {parts[0]}   6-12 px {parts[1]}")
