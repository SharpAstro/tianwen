"""#2203, the measurement its last comment names (2026-11-25): a deblend at Gaia's positions next to detected stars.

    python gaia_deblend.py --store <dataset store> --stem <master stem> --sxt <StarXTerminator plate> --gaia <cache.npz>
        [--cells 1,1 5,4 4,4] [--sigma 1.60] [--bg const|median41] [--out <folder>]

The read in the plan is the --bg median41 run; with a free constant in each disc (the default) every band comes back
unchanged, because the constant takes the light at the band's 9 px scale.

The forced subtraction (gaia_forced.py) found that the only catalogue light carrying the plate's grain is that of missed
Gaia stars 2 to 5 px from a detected star. Here each such detection is refit on the STORED PLATE (master less the
builder's single-star fit) together with its companions:
  - the PSF (round Gaussian, gaia_forced.py's width) at the detection, free sign, plus its x and y derivatives, which
    take up a builder fit pulled toward the companion or of the wrong height;
  - the PSF at each missed companion's Gaia position (shifted by the matched median offset), amplitude >= 0 (an active
    set: a negative one is dropped and the fit redone);
  - a local constant, which is NOT subtracted;
in a disc of 8 px radius about the detection, per channel, by linear least squares. Groups are fitted one after the
other on a working copy, so a later group sees the earlier ones' result.
Detections taken: outcome Subtracted, not saturated, not inpainted (an inpainted core holds the inpaint, not the fit).
Nulls: (1) the free terms alone (no companion); (2) each companion moved to the point mirrored through the detection.
The band is gaia_forced.py's (7 px median, 9 px box less 41 px box, rms in the stored plate's 9 px-mean sigma).
"""
import argparse
import csv
import time

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import median_filter, uniform_filter
from scipy.spatial import cKDTree

p = argparse.ArgumentParser()
p.add_argument("--cells", nargs="+", default=["1,1", "5,4", "4,4"])
p.add_argument("--sigma", type=float, default=1.60, help="PSF Gaussian sigma, px (gaia_forced.py printed 1.60)")
p.add_argument("--radius", type=float, default=8.0)
p.add_argument("--bg", choices=["const", "median41"], default="const",
               help="const: a free local constant in each disc (not subtracted); median41: the plate's 41 px median taken as "
                    "the background before fitting, no constant (what gaia_forced.py's free fit did), so light at the band's "
                    "9 px scale is not absorbed")
p.add_argument("--store", required=True, help="the dataset store (session-masters/, starless/plates/)")
p.add_argument("--stem", required=True, help="the session master's file stem in the store")
p.add_argument("--sxt", required=True, help="StarXTerminator's plate of the same master (FITS)")
p.add_argument("--gaia", required=True, help="the Gaia cache of the field (.npz: ra, dec, bp, as tianwen's cache writes)")
p.add_argument("--out", default=".", help="folder for the per-cell .npz")
a = p.parse_args()
STEM = a.stem
STORE = a.store
MASTER = f"{STORE}/session-masters/{STEM}.fits"
PLATE = f"{STORE}/starless/plates/{STEM}_plate.fits"
STARS = f"{STORE}/starless/plates/{STEM}_plate.stars.csv"
SXT = a.sxt
GAIA = a.gaia
B, PAD = 300, 48
SIG = a.sigma
R = a.radius

t0 = time.time()
m = np.nan_to_num(np.asarray(fits.getdata(MASTER), np.float64))
plate = np.nan_to_num(np.asarray(fits.getdata(PLATE), np.float64))
sxt = np.nan_to_num(np.asarray(fits.getdata(SXT), np.float64))
C, H, W = m.shape
w = WCS(fits.getheader(MASTER), naxis=2)
rows = list(csv.DictReader(open(STARS, encoding="utf-8")))
dxy = np.array([(float(r["x"]), float(r["y"])) for r in rows])
take = np.array([r["outcome"] == "Subtracted" and r["saturated"] != "1" and r["inpainted"] != "1" for r in rows])
dtree = cKDTree(dxy)
z = np.load(GAIA)
gx, gy = w.all_world2pix(z["ra"], z["dec"], 0)
on = (gx >= 0) & (gx < W) & (gy >= 0) & (gy < H) & np.isfinite(z["mag"])
gx, gy, gmag = gx[on], gy[on], z["mag"][on]
d, k = dtree.query(np.stack([gx, gy], 1), distance_upper_bound=2.0)
found = np.isfinite(d)
off = np.median(dxy[k[found]] - np.stack([gx[found], gy[found]], 1), axis=0)
gx, gy = gx + off[0], gy + off[1]
gtree = cKDTree(np.stack([gx, gy], 1))
# Each missed Gaia star's nearest TAKEN detection; a companion when that lies 2 to 5 px away.
mx, my = gx[~found], gy[~found]
ttree = cKDTree(dxy[take])
tidx = np.nonzero(take)[0]
dd, kk = ttree.query(np.stack([mx, my], 1))
comp = (dd >= 2.0) & (dd <= 5.0)
owner = tidx[kk[comp]]
cx, cy = mx[comp], my[comp]
print(f"{len(gmag)} Gaia stars, {found.sum()} matched, offset {off[0]:+.2f} {off[1]:+.2f}; detections taken {take.sum()} of {len(rows)}; "
      f"missed Gaia stars 2 to 5 px from a taken detection: {comp.sum()} (load {time.time() - t0:.0f} s)")


def band(img, ref):
    out = []
    for c in range(C):
        s9 = 1.4826 * np.median(np.abs(np.diff(ref[c, PAD:-PAD, PAD:-PAD], axis=1))) / np.sqrt(2) / 9
        p7 = median_filter(img[c], 7)
        bnd = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:-PAD, PAD:-PAD]
        out.append(np.sqrt(np.mean(bnd ** 2)) / s9)
    return out


def g(xs, ys, fx, fy):
    return np.exp(-((xs - fx) ** 2 + (ys - fy) ** 2) / (2 * SIG ** 2))


def deblend(pl, groups, mode):
    """mode: 'deblend' (companions at Gaia), 'free' (no companions), 'mirror' (companions mirrored)."""
    work = pl.copy()
    bg = np.stack([median_filter(pl[c], 41) for c in range(C)]) if a.bg == "median41" else np.zeros_like(pl)
    side = pl.shape[1]
    shares, n_fit, n_comp_kept = [], 0, 0
    for (fx, fy, peaks, comps) in groups:
        x0, y0 = int(round(fx)), int(round(fy))
        r = int(np.ceil(R))
        ys, xs = np.mgrid[max(0, y0 - r):min(side, y0 + r + 1), max(0, x0 - r):min(side, x0 + r + 1)]
        disc = (xs - fx) ** 2 + (ys - fy) ** 2 <= R * R
        xs, ys = xs[disc], ys[disc]
        if len(xs) < 40:
            continue
        g0 = g(xs, ys, fx, fy)
        cols = [g0, g0 * (xs - fx) / SIG ** 2, g0 * (ys - fy) / SIG ** 2]
        cpos = []
        if mode == "deblend":
            cpos = comps
        elif mode == "mirror":
            cpos = [(2 * fx - qx, 2 * fy - qy) for qx, qy in comps]
        ccols = [g(xs, ys, qx, qy) for qx, qy in cpos]
        n_fit += 1
        for c in range(C):
            y = work[c, ys, xs] - bg[c, ys, xs]
            active = list(range(len(ccols)))
            extra = [np.ones_like(g0)] if a.bg == "const" else []
            while True:
                M = np.stack(cols + [ccols[q] for q in active] + extra, 1)
                coef = np.linalg.lstsq(M, y, rcond=None)[0]
                camp = coef[3:3 + len(active)]
                if len(active) == 0 or camp.min() >= 0:
                    break
                active = [q for q, v in zip(active, camp) if v >= 0]
            nmod = M.shape[1] - len(extra)
            model = M[:, :nmod] @ coef[:nmod]
            work[c, ys, xs] = work[c, ys, xs] - model
            if mode != "free" and c == 1:
                n_comp_kept += len(active)
                for v in camp:
                    if peaks[c] > 0:
                        shares.append(v / peaks[c])
    return work, n_fit, n_comp_kept, shares


side = B + 2 * PAD
summary = []
for cell in a.cells:
    t1 = time.time()
    i, j = (int(v) for v in cell.split(","))
    y0, x0 = i * B - PAD, j * B - PAD
    sl = (slice(None), slice(y0, y0 + side), slice(x0, x0 + side))
    pl, sx, ms = plate[sl], sxt[sl], m[sl]
    # Groups: detections taken that own a companion and lie inside the crop's inner part plus a margin.
    sel = (owner >= 0)
    own_in = np.unique(owner)
    groups = []
    yy, xx = np.mgrid[-8:9, -8:9]
    ring = ((xx ** 2 + yy ** 2) >= 36) & ((xx ** 2 + yy ** 2) <= 64)
    for det in own_in:
        fx, fy = dxy[det, 0] - x0, dxy[det, 1] - y0
        if not (12 <= fx < side - 12 and 12 <= fy < side - 12):
            continue
        q = owner == det
        comps = list(zip(cx[q] - x0, cy[q] - y0))
        ix, iy = int(round(fx)), int(round(fy))
        peaks = [ms[c, iy, ix] - np.median(ms[c, iy - 8:iy + 9, ix - 8:ix + 9][ring]) for c in range(C)]
        groups.append((fx, fy, peaks, comps))
    # Mirrored points with a Gaia star within 1 px (the null is then not empty sky).
    mir = [(2 * gx_ - qx, 2 * gy_ - qy) for gx_, gy_, _, cs in groups for qx, qy in cs]
    mir_hit = sum(1 for (qx, qy) in mir if gtree.query([qx + x0, qy + y0])[0] <= 1.0)
    out = {}
    for mode in ("deblend", "free", "mirror"):
        work, nf, nk, shares = deblend(pl, groups, mode)
        out[mode] = (work, nf, nk, shares)
    ncomp = sum(len(cs) for *_, cs in groups)
    inner = (slice(None), slice(PAD, -PAD), slice(PAD, -PAD))
    print(f"\ncell {i},{j}: {len(groups)} detections refit with {ncomp} companions (green: {out['deblend'][2]} kept >= 0); "
          f"mirrored points with a Gaia star within 1 px: {mir_hit} of {len(mir)}; {time.time() - t1:.0f} s")
    sh = np.array(out["deblend"][3])
    if len(sh):
        print(f"  companion amplitude / detection's own peak in the master (green, kept companions): median {np.median(sh):.3f}, "
              f"quartiles {np.percentile(sh, 25):.3f} to {np.percentile(sh, 75):.3f}")
    rowsb = [("stored plate", pl), ("deblend (detection + companions)", out["deblend"][0]),
             ("null: free terms alone", out["free"][0]), ("null: companions mirrored", out["mirror"][0]), ("StarXTerminator", sx)]
    for label, img in rowsb:
        b_ = band(img, pl)
        print(f"  {label:34s} " + " / ".join(f"{v:4.1f}" for v in b_))
        summary.append((cell, label, b_))
    for mode in ("deblend", "free", "mirror"):
        diff = pl - out[mode][0]
        print(f"  model ({mode}) own band " + " / ".join(f"{v:4.1f}" for v in band(diff, pl))
              + f"; light removed over the inner cell, share of the plate's sum over its median: "
              + " / ".join(f"{diff[c][PAD:-PAD, PAD:-PAD].sum() / max(1e-12, (pl[c][PAD:-PAD, PAD:-PAD] - np.median(pl[c][PAD:-PAD, PAD:-PAD])).clip(0).sum()) * 100:5.1f} %" for c in range(C)))
    np.savez_compressed(f"{a.out}/gaia-deblend-{a.bg}-{i}-{j}.npz", plate=pl.astype(np.float32),
                        deblend=out["deblend"][0].astype(np.float32), free=out["free"][0].astype(np.float32),
                        mirror=out["mirror"][0].astype(np.float32), sxt=sx.astype(np.float32))
print(f"\ntotal {time.time() - t0:.0f} s")
