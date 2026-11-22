"""Is the grain ours keeps over the judge faint stars, over a whole held-out master? (2026-11-22, #2203.)

grain_gaia.py's test on every 300 px cell of the master (48 px margin): the grain of (ours less SXT) on the channels' mean
(9 px box mean less 41 px) against the light of the Gaia DR3 stars (BP 18 to 21, from the Vizier cache) that the
scorer's detector did not find (no source within 2 px), each a Gaussian of 1.2 px at 10^(-0.4 BP); and the same with the
stars shifted by 37 px (the null). Cells are grouped by how crowded they are (Gaia stars to BP 21 per cell) and by the
nebula's level (the cell's median over the frame's sky, in the sky noise).

    python -I grain_gaia_whole.py <master> <sources.csv> <ours starless> <sxt starless> <gaia cache npz>
"""
import csv
import sys

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import gaussian_filter, uniform_filter
from scipy.spatial import cKDTree

MASTER, SOURCES, OURS, SXT, CACHE = sys.argv[1:6]
B, PAD = 300, 48


def lum(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return np.nan_to_num(a[:3].mean(axis=0) if a.ndim == 3 else a)


def grain(a):
    return (uniform_filter(a, 9) - uniform_filter(a, 41))[PAD:-PAD, PAD:-PAD]


m, ours, sxt = lum(MASTER), lum(OURS), lum(SXT)
H, W = m.shape
w = WCS(fits.getheader(MASTER), naxis=2)
noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
z = np.load(CACHE)
gx, gy = w.all_world2pix(z["ra"], z["dec"], 0)
on = (gx >= 0) & (gx < W) & (gy >= 0) & (gy < H) & np.isfinite(z["mag"])
gx, gy, gmag = gx[on], gy[on], z["mag"][on]
det = np.array([(float(r["x"]), float(r["y"])) for r in csv.DictReader(open(SOURCES, encoding="utf-8"))])
d, _ = cKDTree(det).query(np.stack([gx, gy], 1), distance_upper_bound=2.0)
undet = ~np.isfinite(d)
print(f"{len(gmag)} Gaia stars to BP 21 on the frame, {len(det)} sources detected, {undet.sum()} Gaia stars undetected")
side = B + 2 * PAD
sky = None
rows = []
for i in range(1, (H - PAD) // B):
    for j in range(1, (W - PAD) // B):
        y0, x0 = i * B - PAD, j * B - PAD
        if y0 + side > H or x0 + side > W:
            continue
        sl = (slice(y0, y0 + side), slice(x0, x0 + side))
        if (m[sl] == 0).mean() > 0.01:
            continue
        inside = (gx >= x0) & (gx < x0 + side) & (gy >= y0) & (gy < y0 + side)
        sel = inside & undet & (gmag >= 18)
        if sel.sum() < 50:
            continue

        def light(dx=0.0, dy=0.0):
            a = np.zeros((side, side), np.float32)
            qx = np.round(gx[sel] - x0 + dx).astype(int) % side
            qy = np.round(gy[sel] - y0 + dy).astype(int) % side
            np.add.at(a, (qy, qx), (10 ** (-0.4 * (gmag[sel] - 20))).astype(np.float32))
            return grain(gaussian_filter(a, 1.2))

        g = grain(ours[sl] - sxt[sl])
        lt, nl = light(), light(37, 37)
        rows.append((i, j, int(inside.sum()), float(np.median(m[sl])), np.corrcoef(g.ravel(), lt.ravel())[0, 1],
                     np.corrcoef(g.ravel(), nl.ravel())[0, 1], g.std() / noise))
a = np.array(rows)
sky = float(np.percentile(a[:, 3], 5))
a[:, 3] = (a[:, 3] - sky) / noise
print(f"{len(a)} cells; frame sky noise {noise:.3g}")
print("cells by crowding (Gaia stars to BP 21 per 396 px cell) and nebula level (cell median over the sky, sky sigma):"
      " median correlation of the grain (ours less sxt) with the undetected stars' light, BP 18 to 21 [shifted null];"
      " grain rms in sky sigma; cells")
dens = np.percentile(a[:, 2], [33.3, 66.7])
for dname, dsel in (("least crowded third", a[:, 2] < dens[0]), ("middle third", (a[:, 2] >= dens[0]) & (a[:, 2] < dens[1])),
                    ("most crowded third", a[:, 2] >= dens[1])):
    for nname, nsel in (("level under 5", a[:, 3] < 5), ("5 to 20", (a[:, 3] >= 5) & (a[:, 3] < 20)), ("20 and over", a[:, 3] >= 20)):
        s = dsel & nsel
        if s.sum() == 0:
            continue
        print(f"  {dname:20s} ({np.median(a[s, 2]):6.0f} stars) nebula {nname:13s}: corr {np.median(a[s, 4]):+.2f} "
              f"[{np.median(a[s, 5]):+.2f}], over 0.3 in {(a[s, 4] > 0.3).mean() * 100:3.0f} %; grain rms {np.median(a[s, 6]):.2f}; {s.sum()} cells")
np.save(sys.argv[1].rsplit("/", 1)[-1].replace(".fits", "") + "_grain_gaia_cells.npy", a)
