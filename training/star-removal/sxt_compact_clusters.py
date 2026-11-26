"""What each remover keeps of the LMC's compact clusters on a held-out master (the free check before the crowded arms).

    python -I sxt_compact_clusters.py <clusters.csv> <master.fits> builder=<plate> judge=<plate> <label>=<plate> ...

Our synthetic scenes call a compact cluster (half-light radius under 16 px on the ASI585 master they are drawn from, at
2.07 arcsec a pixel) all stars, so a remover trained on them is asked to take a cluster's unresolved light whole. This reads
what StarXTerminator (the judge) does with the real ones.

The clusters: `lmc-clusters.csv` beside this script, the open and globular clusters of OpenNGC in and about 30 Doradus
(OpenNGC's name, type, position in degrees and major and minor axes in arcmin), extracted 2026-10-20 when the scenes'
groups were sized. The table of 2026-11-26 was read on the held-out Tarantula master (the SV605CC of 2026-10-17), with the
plate builder's plate, StarXTerminator's, and R4h's two seeds as trained and through the chain, all of that master.

Per catalogued cluster inside the frame, on the channels' mean: the light inside one catalogue radius above the median of a
ring at 1.5 to 2 radii, on each plate. The plate builder's (`builder`) takes resolved stars only, so its excess is the
cluster's unresolved light and any star its finder missed; each plate's excess over the builder's is the share of that
light it keeps (1 kept as background, 0 taken as stars). A cluster counts when the builder's excess is at least 10 times
its noise (the ring's pixel scatter times the square root of the disc's pixel count), and is compact when the half-light
radius of the builder's excess is under the scenes' 16 px on this master's grid (16 x 2.07 / this master's scale). Per
plate: the median kept share and its middle half, over every counted cluster and over the compact ones; and against the
judge (`judge`), per cluster, the median of (plate less judge) and in how many clusters the plate keeps less. Reads only.
"""
import csv
import sys

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS

SCENE_COMPACT_PX, SCENE_SCALE = 16.0, 2.07
rows = list(csv.DictReader(open(sys.argv[1])))
h = fits.getheader(sys.argv[2])
w = WCS(h, naxis=2)
scale = np.sqrt(np.abs(np.linalg.det(w.pixel_scale_matrix))) * 3600
compact_px = SCENE_COMPACT_PX * SCENE_SCALE / scale


def lum(path):
    a = np.asarray(fits.getdata(path), np.float64)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


images = [("input", lum(sys.argv[2]))] + [(lab, lum(p)) for lab, p in (a.split("=", 1) for a in sys.argv[3:])]
names = [n for n, _ in images]
plates = [n for n in names if n not in ("input", "builder")]
H, W = images[0][1].shape


def excess(img, x, y, R, half=False):
    yy, xx = np.mgrid[int(y - 2 * R):int(y + 2 * R) + 1, int(x - 2 * R):int(x + 2 * R) + 1]
    cut = img[yy, xx]
    rr = np.hypot(xx - x, yy - y)
    ring = cut[(rr >= 1.5 * R) & (rr < 2 * R)]
    bg = np.median(ring)
    inside = rr < R
    ex = cut[inside] - bg
    total = float(ex.sum())
    noise = float(1.4826 * np.median(np.abs(ring - bg)) * np.sqrt(inside.sum()))
    if not half:
        return total, noise
    order = np.argsort(rr[inside])
    cum = np.cumsum(ex[order])
    hl = float(rr[inside][order][min(np.searchsorted(cum, total / 2), len(cum) - 1)]) if total > 0 else float("nan")
    return total, noise, hl


print(f"{len(rows)} catalogued clusters; plate scale {scale:.2f} arcsec/px; compact: half-light radius under {compact_px:.1f} px "
      f"(the scenes' 16 px at {SCENE_SCALE} arcsec/px); kept share = plate excess / builder excess")
print(f"{'cluster':10s} {'diam px':>7s} {'hl px':>5s} {'S/N':>5s} " + " ".join(f"{n:>11s}" for n in plates))
kept, compact = {n: [] for n in plates}, []
for r in rows:
    x, y = w.all_world2pix([[float(r["ra_deg"]), float(r["dec_deg"])]], 0)[0]
    R = float(r["major_arcmin"]) * 60 / scale / 2
    if not (2 * R + 5 < x < W - 2 * R - 5 and 2 * R + 5 < y < H - 2 * R - 5) or R < 3:
        continue
    base, noise, hl = excess(dict(images)["builder"], x, y, R, half=True)
    if base < 10 * noise:
        continue
    shares = {n: excess(img, x, y, R)[0] / base for n, img in images if n in plates}
    for n in plates:
        kept[n].append(shares[n])
    compact.append(hl < compact_px)
    print(f"{r['index']:10s} {2 * R:7.1f} {hl:5.1f} {base / noise:5.0f} " + " ".join(f"{shares[n]:11.2f}" for n in plates))

compact = np.array(compact)
judge = np.array(kept["judge"])
print(f"\n{len(compact)} clusters at S/N 10 or over, {compact.sum()} of them compact")
for subset, sel in (("all", np.ones_like(compact)), ("compact", compact)):
    print(f"{subset}: kept share median (p25 to p75); against the judge per cluster, median of the difference and how many keep less")
    for n in plates:
        v = np.array(kept[n])[sel]
        line = f"  {n:18s} {np.median(v):5.2f} ({np.percentile(v, 25):5.2f} to {np.percentile(v, 75):5.2f})"
        if n != "judge":
            d = v - judge[sel]
            line += f"   {np.median(d):+5.2f}, keeps less in {int((d < 0).sum())} of {len(d)}"
        print(line)
