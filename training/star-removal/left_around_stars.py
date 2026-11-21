"""What our plate holds that StarXTerminator's does not, stacked about the catalogued stars of a crowded session.

    python -I left_around_stars.py <master.fits> <plate stars.csv> <reference starless.fits> <label>=<starless.fits> ...

For each image: (image less the reference), channel mean, stacked about the catalogued stars in bins of significance
(saturated ones left out), as a radial profile in units of the master's pixel noise, less the same stack about as many
random places (the level the difference has anywhere). Beside it the master's own stack (master less reference), which
is the star, so the share of each star's light an image still holds can be read ring by ring. Says whether what is left
sits ON the catalogued stars (their wings, a share of each) or between them.
"""
import csv
import sys

import numpy as np
from astropy.io import fits

R = 24
BINS = ((5, 7), (7, 10), (10, 20), (20, 40), (40, 100), (100, 300))
RINGS = ((0, 1.5), (1.5, 3), (3, 5), (5, 8), (8, 12), (12, 18), (18, 24))


def lum(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return a.mean(axis=0) if a.ndim == 3 else a


def stack(img, xs, ys):
    h, w = img.shape
    out = np.zeros((2 * R + 1, 2 * R + 1), np.float64)
    seen = np.zeros((2 * R + 1, 2 * R + 1), np.float64)
    n = 0
    for x, y in zip(xs, ys):
        xi, yi = int(round(x)), int(round(y))
        if xi < R or yi < R or xi >= w - R or yi >= h - R:
            continue
        cut = img[yi - R:yi + R + 1, xi - R:xi + R + 1]
        ok = np.isfinite(cut)
        out += np.where(ok, cut, 0)
        seen += ok
        n += 1
    return out / np.maximum(seen, 1), n


def rings(st):
    yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
    r = np.hypot(xx, yy)
    return [float(st[(r >= a) & (r < b)].mean()) for a, b in RINGS]


def main():
    master, ref = lum(sys.argv[1]), lum(sys.argv[3])
    rows = list(csv.DictReader(open(sys.argv[2], encoding="utf-8")))
    sig = np.array([float(r["significance"]) for r in rows])
    x = np.array([float(r["x"]) for r in rows]); y = np.array([float(r["y"]) for r in rows])
    keep = np.array([r["saturated"] != "1" for r in rows])
    noise = float(np.nanmedian([float(r["sigma"]) for r in rows]))
    images = [("the master", master)] + [(a.split("=", 1)[0], lum(a.split("=", 1)[1])) for a in sys.argv[4:]]
    rng = np.random.default_rng(3)
    h, w = master.shape
    print(f"{len(rows)} catalogued stars; pixel noise {noise:.5f}; rings (px): " + ", ".join(f"{a:g}-{b:g}" for a, b in RINGS))
    for lo, hi in BINS:
        m = keep & (sig >= lo) & (sig < hi)
        rx, ry = rng.random(int(m.sum())) * w, rng.random(int(m.sum())) * h
        print(f"stars of {lo} to {hi} sigma: {int(m.sum())}")
        star = None
        for label, img in images:
            d = (img - ref) / noise
            st, n = stack(d, x[m], y[m])
            base, _ = stack(d, rx, ry)
            prof = np.array(rings(st)) - np.array(rings(base))
            if star is None:
                star = prof
                print(f"  {label:28s} " + " ".join(f"{v:8.2f}" for v in prof) + "   (sigma, the star itself)")
            else:
                share = " ".join(f"{100 * v / s:7.0f}%" if abs(s) > 0.02 else "       -" for v, s in zip(prof, star))
                print(f"  {label:28s} " + " ".join(f"{v:8.2f}" for v in prof) + "   share of the star: " + share)


main()
