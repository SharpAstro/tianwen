"""What each remover keeps of the LMC's compact clusters on a held-out master (the free check before the crowded arms).

    python -I sxt_compact_clusters.py <clusters.csv> <master.fits> <label>=<starless plate> ...

Our synthetic scenes call a compact cluster (half-light radius under 16 px) all stars, so a remover trained on them is
asked to take a cluster's unresolved light whole. This reads what StarXTerminator (the judge) does with the real ones.

Per catalogued cluster inside the frame (lmc-clusters.csv, OpenNGC's LMC clusters), on the channels' mean: the light
inside one catalogue radius above the median of a ring at 1.5 to 2 radii, on the input and on each plate. The plate
builder's (label `builder`) takes resolved stars only, so its excess is the cluster's unresolved light; each plate's
excess over the builder's is the share of that light it keeps (1 = kept as background, 0 = taken as stars). A cluster
counts when the builder's excess is at least 10 times its own noise (the ring's pixel scatter times the square root of
the disc's pixel count). Reads only.
"""
import csv
import sys

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS

rows = list(csv.DictReader(open(sys.argv[1])))
h = fits.getheader(sys.argv[2])
w = WCS(h, naxis=2)
scale = np.sqrt(np.abs(np.linalg.det(w.pixel_scale_matrix))) * 3600


def lum(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


images = [("input", lum(sys.argv[2]))] + [(lab, lum(p)) for lab, p in (a.split("=", 1) for a in sys.argv[3:])]
names = [n for n, _ in images]
H, W = images[0][1].shape


def excess(img, x, y, R):
    yy, xx = np.mgrid[int(y - 2 * R):int(y + 2 * R) + 1, int(x - 2 * R):int(x + 2 * R) + 1]
    cut = img[yy, xx]
    rr = np.hypot(xx - x, yy - y)
    ring = cut[(rr >= 1.5 * R) & (rr < 2 * R)]
    bg = np.median(ring)
    inside = rr < R
    noise = 1.4826 * np.median(np.abs(ring - bg)) * np.sqrt(inside.sum())
    return float((cut[inside] - bg).sum()), float(noise)


print(f"{len(rows)} catalogued clusters; plate scale {scale:.2f} arcsec/px; kept share = plate excess / builder excess")
print(f"{'cluster':10s} {'diam px':>7s} {'S/N':>5s} " + " ".join(f"{n:>12s}" for n in names[1:]))
kept = {n: [] for n in names[1:]}
for r in rows:
    x, y = w.all_world2pix([[float(r["ra_deg"]), float(r["dec_deg"])]], 0)[0]
    R = float(r["major_arcmin"]) * 60 / scale / 2
    if not (2 * R + 5 < x < W - 2 * R - 5 and 2 * R + 5 < y < H - 2 * R - 5) or R < 3:
        continue
    values = {n: excess(img, x, y, R) for n, img in images}
    base, noise = values["builder"]
    if base < 10 * noise:
        continue
    shares = {n: values[n][0] / base for n in names[1:]}
    for n in names[1:]:
        kept[n].append(shares[n])
    print(f"{r['index']:10s} {2 * R:7.1f} {base / noise:5.0f} " + " ".join(f"{shares[n]:12.2f}" for n in names[1:]))
print(f"\n{len(kept['builder'])} clusters at S/N 10 or over; kept share, median (p25 to p75):")
for n in names[1:]:
    v = np.array(kept[n])
    print(f"  {n:18s} {np.median(v):5.2f} ({np.percentile(v, 25):5.2f} to {np.percentile(v, 75):5.2f})")
