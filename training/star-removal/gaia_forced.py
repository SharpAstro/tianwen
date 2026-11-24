"""#2203 by the catalogue, the first measurement (2026-11-22): subtract from the stored truth plate the Gaia stars the plate
finder missed, at their catalogue positions, and read what it does to the crowded grain and to the nebula.

    python gaia_forced.py --store <dataset store> --stem <master stem> --sxt <StarXTerminator plate> --gaia <cache.npz>
        [--cells 1,1 5,4 4,4] [--out <folder>]

The Tarantula session of 2024-10-02 (the 2026-10-12 store), band.py's cells (300 px, 48 px margin). Per channel:
  - the PSF: a round Gaussian whose width is the second moment of the master's moderate isolated detections, stacked;
  - Gaia DR3 (BP to 21, the Vizier cache) placed by the master's WCS, shifted by the median offset of the matched
    detections; "missed" is no detection within 2 px;
  - A, the catalogue's light: each missed star at 10^(-0.4 (BP - 20)) times one scale per channel, the median ratio of
    the master's peak over the annulus to that figure for matched moderate detections. Nothing is fitted to the plate, so
    the model cannot take the nebula;
  - B, a free fit: non-negative amplitudes for every missed star, least squares on the plate less its 41 px median.
Printed per cell and model: band.py's band (7 px median, 9 px box less 41 px box, rms in the plate's 9 px-mean sigma),
R / G / B, beside the stored plate's and StarXTerminator's; and the light each model removes, as a share of the plate's.
"""
import argparse
import csv

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy import sparse
from scipy.ndimage import median_filter, uniform_filter, maximum_filter
from scipy.sparse.linalg import lsmr
from scipy.spatial import cKDTree

p = argparse.ArgumentParser()
p.add_argument("--cells", nargs="+", default=["1,1", "5,4", "4,4"])
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

m = np.nan_to_num(np.asarray(fits.getdata(MASTER), np.float64))
plate = np.nan_to_num(np.asarray(fits.getdata(PLATE), np.float64))
sxt = np.nan_to_num(np.asarray(fits.getdata(SXT), np.float64))
C, H, W = m.shape
w = WCS(fits.getheader(MASTER), naxis=2)
rows = list(csv.DictReader(open(STARS, encoding="utf-8")))
det = np.array([(float(r["x"]), float(r["y"]), float(r["significance"]), r["saturated"] == "1") for r in rows], dtype=object)
dxy = np.array(det[:, :2], float)
dsig = np.array(det[:, 2], float)
dsat = np.array(det[:, 3], bool)
dtree = cKDTree(dxy)
z = np.load(GAIA)
gx, gy = w.all_world2pix(z["ra"], z["dec"], 0)
on = (gx >= 0) & (gx < W) & (gy >= 0) & (gy < H) & np.isfinite(z["mag"])
gx, gy, gmag = gx[on], gy[on], z["mag"][on]
d, k = dtree.query(np.stack([gx, gy], 1), distance_upper_bound=2.0)
found = np.isfinite(d)
off = np.median(dxy[k[found]] - np.stack([gx[found], gy[found]], 1), axis=0)
gx, gy = gx + off[0], gy + off[1]
print(f"{len(gmag)} Gaia stars on the frame; {found.sum()} matched; median offset detection less Gaia {off[0]:+.2f}, {off[1]:+.2f} px")

# The PSF and the scale, from matched moderate isolated detections (significance 20 to 200, not saturated).
lum = m.mean(axis=0)
pick = [j for j in np.nonzero(found)[0] if 20 <= dsig[k[j]] <= 200 and not dsat[k[j]]]
pick = [j for j in pick if 12 <= gx[j] < W - 12 and 12 <= gy[j] < H - 12][:4000]
yy, xx = np.mgrid[-6:7, -6:7]
ring = ((xx ** 2 + yy ** 2) >= 36) & ((xx ** 2 + yy ** 2) <= 64)
stack = np.zeros((13, 13))
ratios = [[] for _ in range(C)]
for j in pick:
    x, y = int(round(dxy[k[j], 0])), int(round(dxy[k[j], 1]))
    cut = lum[y - 6:y + 7, x - 6:x + 7]
    b0 = np.median(cut[ring])
    h = cut[6, 6] - b0
    if h <= 0:
        continue
    stack += (cut - b0) / h
    f = 10 ** (-0.4 * (gmag[j] - 20))
    for c in range(C):
        cc = m[c, y - 6:y + 7, x - 6:x + 7]
        ratios[c].append((cc[6, 6] - np.median(cc[ring])) / f)
stack /= max(1, len(pick))
inner = (xx ** 2 + yy ** 2) <= 16
wts = np.clip(stack, 0, None) * inner
sigma = float(np.sqrt((wts * (xx ** 2 + yy ** 2)).sum() / wts.sum() / 2))
scale = [float(np.median(r)) for r in ratios]
print(f"PSF: Gaussian sigma {sigma:.2f} px (FWHM {2.355 * sigma:.2f}) from {len(pick)} detections; peak per 10^(-0.4 (BP-20)): "
      + " / ".join(f"{s:.3g}" for s in scale))


def stamp(fx, fy, side):
    """Rows, columns and values of a peak-1 Gaussian at (fx, fy), 9x9, clipped to the crop."""
    x0, y0 = int(round(fx)), int(round(fy))
    ys, xs = np.mgrid[y0 - 4:y0 + 5, x0 - 4:x0 + 5]
    v = np.exp(-((xs - fx) ** 2 + (ys - fy) ** 2) / (2 * sigma ** 2))
    ok = (ys >= 0) & (ys < side) & (xs >= 0) & (xs < side)
    return ys[ok], xs[ok], v[ok]


def band(img, ref):
    """band.py's figure for a 396 px crop, in the reference plate's 9 px-mean sigma (per channel)."""
    out = []
    for c in range(C):
        s9 = 1.4826 * np.median(np.abs(np.diff(ref[c, PAD:-PAD, PAD:-PAD], axis=1))) / np.sqrt(2) / 9
        p7 = median_filter(img[c], 7)
        bnd = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:-PAD, PAD:-PAD]
        out.append(np.sqrt(np.mean(bnd ** 2)) / s9)
    return out


side = B + 2 * PAD
for cell in a.cells:
    i, j = (int(v) for v in cell.split(","))
    y0, x0 = i * B - PAD, j * B - PAD
    sl = (slice(None), slice(y0, y0 + side), slice(x0, x0 + side))
    pl, sx = plate[sl], sxt[sl]
    sel = (~found) & (gx >= x0 - 4) & (gx < x0 + side + 4) & (gy >= y0 - 4) & (gy < y0 + side + 4)
    px, py, pm = gx[sel] - x0, gy[sel] - y0, gmag[sel]
    rr, cc, vv, col = [], [], [], []
    for n, (fx, fy) in enumerate(zip(px, py)):
        r_, c_, v_ = stamp(fx, fy, side)
        rr.append(r_ * side + c_); vv.append(v_); col.append(np.full(len(v_), n))
    A = sparse.csr_matrix((np.concatenate(vv), (np.concatenate(rr), np.concatenate(col))), shape=(side * side, len(px)))
    flux = 10 ** (-0.4 * (pm - 20))
    modelA = np.stack([(A @ (scale[c] * flux)).reshape(side, side) for c in range(C)])
    modelB = []
    for c in range(C):
        target = (pl[c] - median_filter(pl[c], 41)).ravel()
        # An unbounded sparse least squares, then the negative amplitudes set to zero (the bounded solver took over half
        # an hour a cell); the clip is what keeps a missed star from digging into the plate.
        amp = lsmr(A, target, maxiter=300)[0]
        modelB.append((A @ np.clip(amp, 0, None)).reshape(side, side))
    modelB = np.stack(modelB)
    inner = (slice(None), slice(PAD, -PAD), slice(PAD, -PAD))
    light = float(np.sum(pl[inner] - np.median(pl[inner], axis=(1, 2), keepdims=True).clip(0)))
    print(f"\ncell {i},{j}: {len(px)} missed Gaia stars (median BP {np.median(pm):.1f}); band R / G / B in the plate's 9 px-mean sigma")
    for label, img in (("stored plate", pl), ("plate less A (catalogue light)", pl - modelA), ("plate less B (free fit)", pl - modelB),
                       ("StarXTerminator", sx)):
        print(f"  {label:32s} " + " / ".join(f"{v:4.1f}" for v in band(img, pl)))
    for label, mdl in (("A", modelA), ("B", modelB)):
        print(f"  model {label}: removes {mdl[inner].sum() / max(light, 1e-12) * 100:5.1f} % of the plate's light over its median;"
              f" its own band " + " / ".join(f"{v:4.1f}" for v in band(mdl, pl)))
    np.savez_compressed(f"{a.out}/gaia-forced-{i}-{j}.npz", plate=pl.astype(np.float32), a=modelA.astype(np.float32),
                        b=modelB.astype(np.float32), sxt=sx.astype(np.float32))
