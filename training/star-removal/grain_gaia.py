"""Is the crowded grain faint stars? (2026-11-22, the input-grain arm's check, #2203.)

    python -I grain_gaia.py <session master> <stored plate> <plate stars.csv> <ours starless> <sxt starless> <gaia npz>
                            [--cells i,j ...]

On one session master (the plan's runs: the Tarantula session of 2024-10-02 in the 2026-10-12 store, cells 1,1, 5,4 and
4,4 of band.py's 300 px grid with its 48 px margin) the grain each map holds (9 px box mean less 41 px, channels' mean)
against Gaia DR3 stars (BP; the npz is gaia_vizier.fetch_box's cache file, arrays ra, dec and mag, which must cover the
frame) split in two: those the plate's finder detected (a stars.csv star within 2 px) and those it did not. For each grain
map (ours less SXT: the mottle the judge takes and we keep; the stored plate's own; the master's, the control) and each
Gaia set:
  - the correlation of the grain map with the set's light (each star a Gaussian of 1.2 px at 10^(-0.4 BP), through the
    same box means), and the same with the set shifted by 37 px (the null);
  - the share of the grain map's power within 3 px of the set's stars beside the share of the cell's area those pixels
    are (in a crowded cell the undetected stars cover nearly all of it, so there the correlation is what says it).
"""
import argparse
import csv

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.ndimage import gaussian_filter, uniform_filter
from scipy.spatial import cKDTree

p = argparse.ArgumentParser()
for name in ("master", "plate", "stars", "ours", "sxt", "gaia"):
    p.add_argument(name)
p.add_argument("--cells", nargs="+", default=["1,1", "5,4", "4,4"])
a = p.parse_args()
B, PAD, NEAR = 300, 48, 3
CELLS = [tuple(int(v) for v in c.split(",")) for c in a.cells]


def lum(path):
    x = np.asarray(fits.getdata(path), np.float32)
    return np.nan_to_num(x[:3].mean(axis=0) if x.ndim == 3 else x)


def grain(x):
    return (uniform_filter(x, 9) - uniform_filter(x, 41))[PAD:-PAD, PAD:-PAD]


m, plate, ours, sxt = lum(a.master), lum(a.plate), lum(a.ours), lum(a.sxt)
if not (m.shape == plate.shape == ours.shape == sxt.shape):
    raise SystemExit(f"shapes differ: master {m.shape}, plate {plate.shape}, ours {ours.shape}, sxt {sxt.shape}")
w = WCS(fits.getheader(a.master), naxis=2)
det = np.array([(float(r["x"]), float(r["y"]), float(r["significance"])) for r in csv.DictReader(open(a.stars, encoding="utf-8"))])
dtree = cKDTree(det[:, :2])
z = np.load(a.gaia)
GRA, GDEC, GMAG = z["ra"], z["dec"], z["mag"]
c = w.all_pix2world([[0, 0], [m.shape[1], 0], [0, m.shape[0]], [m.shape[1], m.shape[0]]], 0)
if not (GRA.min() < c[:, 0].min() and GRA.max() > c[:, 0].max() and GDEC.min() < c[:, 1].min() and GDEC.max() > c[:, 1].max()):
    raise SystemExit("the Gaia field does not cover this frame")
side = B + 2 * PAD
yy, xx = np.mgrid[0:side, 0:side]
for i, j in CELLS:
    y0, x0 = i * B - PAD, j * B - PAD
    sl = (slice(y0, y0 + side), slice(x0, x0 + side))
    corners = w.all_pix2world([[x0, y0], [x0 + side, y0], [x0, y0 + side], [x0 + side, y0 + side]], 0)
    ra, dec = corners[:, 0], corners[:, 1]
    box = (GRA >= ra.min() - 0.01) & (GRA <= ra.max() + 0.01) & (GDEC >= dec.min() - 0.01) & (GDEC <= dec.max() + 0.01)
    px, py = w.all_world2pix(GRA[box], GDEC[box], 0)
    gmag = GMAG[box]
    px, py = px - x0, py - y0
    on = (px >= 0) & (px < side) & (py >= 0) & (py < side) & np.isfinite(gmag)
    px, py, gmag = px[on], py[on], gmag[on]
    d, k = dtree.query(np.stack([px + x0, py + y0], 1), distance_upper_bound=2.0)
    found = np.isfinite(d)
    sig_found = det[k[found], 2]
    edge = (sig_found >= 5) & (sig_found < 7)
    lim = float(np.median(gmag[found][edge])) if edge.sum() >= 5 else float("nan")
    print(f"cell {i},{j}: {len(gmag)} Gaia stars, {found.sum()} detected by the plate's finder, {(~found).sum()} not; BP of "
          f"the detected ones at 5 to 7 sigma (the finder's limit): median {lim:.1f}; the undetected: median BP "
          f"{np.median(gmag[~found]):.1f}, {((~found) & (gmag < lim)).sum()} of them brighter than that limit")
    maps = {"ours less sxt": ours[sl] - sxt[sl], "the stored plate": plate[sl], "the master (control)": m[sl]}
    noise = 1.4826 * float(np.median(np.abs(np.diff(m[sl][PAD:-PAD, PAD:-PAD], axis=1)))) / np.sqrt(2)

    def light(sel, dx=0.0, dy=0.0):
        img = np.zeros((side, side), np.float32)
        qx, qy = np.round(px[sel] + dx).astype(int) % side, np.round(py[sel] + dy).astype(int) % side
        np.add.at(img, (qy, qx), (10 ** (-0.4 * (gmag[sel] - 20))).astype(np.float32))
        return grain(gaussian_filter(img, 1.2))

    def near_mask(sel):
        dd, _ = cKDTree(np.stack([px[sel], py[sel]], 1)).query(np.stack([xx.ravel(), yy.ravel()], 1), distance_upper_bound=NEAR)
        return np.isfinite(dd).reshape(side, side)[PAD:-PAD, PAD:-PAD]

    sets = {"detected": found, "undetected": ~found, "undetected, BP 18 and fainter": (~found) & (gmag >= 18)}
    lights = {name: (light(sel), light(sel, 37, 37)) for name, sel in sets.items()}
    nears = {name: near_mask(sel) for name, sel in sets.items()}
    for mname, mp in maps.items():
        g = grain(mp)
        g = g - g.mean()
        pw = g ** 2
        parts = []
        for name in sets:
            lt, nl = lights[name]
            nm = nears[name]
            parts.append(f"{name}: corr {np.corrcoef(g.ravel(), lt.ravel())[0, 1]:+.2f} (shifted "
                         f"{np.corrcoef(g.ravel(), nl.ravel())[0, 1]:+.2f}), power {pw[nm].sum() / pw.sum() * 100:.0f} % on "
                         f"{nm.mean() * 100:.0f} % of the area")
        print(f"  {mname:22s} rms {g.std() / noise:5.2f} sigma | " + " | ".join(parts))
