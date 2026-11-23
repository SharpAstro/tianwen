"""Where the Gaia BP 17 to 19 stars a plate leaves sit: gaia_compact.py's "level set aside" figure of that bin,
split by the nebula under the star, by the star's own light and by how crowded its place is (2026-11-23, a narrowing
of the scorecard's BP row).

    python -I gaia_bp_by_level.py <input master.fits> <check2 work dir> <label=starless.fits> [...]

The stars, the "seen" test and the share taken are gaia_compact.py's (green channel, 3 px disc over the 16 to 24 px
annulus, the same 40,000 drawn from the bin with the same seed). Added per star:
  - the nebula under it: its 32 px block's median over the frame's sky (the blocks' 5th percentile), in sky sigma
    (nebula_stars_row.py's level, on the green channel);
  - its light: the disc's excess over its annulus, in sky sigma;
  - the nearest other catalogued star (to BP 19) and the nearest brighter one, in px;
  - how crowded its place is: the catalogued stars within 16 px of it, in thirds of that count.
Printed per plate: stars and the share removed over half, by each of these, and the level crossed with the crowding
for the stars of 20 to 150 sky sigma. With two plates or more, a last line on the stars the first removes and the
second leaves. In a field as dense as the Tarantula's the level IS mostly the crowd (the two go together at 0.74
there), which is why the cross table is the one to read. Reads only.
"""
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy.spatial import cKDTree

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'denoise'))
import gaia_vizier  # noqa: E402

inp, work = Path(sys.argv[1]), Path(sys.argv[2])
removers = [(a.split("=", 1)[0], Path(a.split("=", 1)[1])) for a in sys.argv[3:]]
hdr = fits.getheader(inp)
if hdr.get("PIXORIG") != 1:
    sys.exit(f"{inp.name}: no PIXORIG = 1, so the WCS convention is unknown; refusing")
data = fits.getdata(inp).astype(np.float64)
H, W = data.shape[-2:]
g = data[1]
w = WCS(hdr, naxis=2)
corners = w.all_pix2world([[0, 0], [W - 1, 0], [0, H - 1], [W - 1, H - 1]], 0)
ra, dec = corners[:, 0], corners[:, 1]
gra, gdec, gmag = gaia_vizier.fetch_box((ra.min() + ra.max()) / 2, (dec.min() + dec.max()) / 2,
                                        ra.max() - ra.min() + 0.1, dec.max() - dec.min() + 0.1, mag_max=19.0)
px, py = w.all_world2pix(gra, gdec, 0)
on = (px >= 0) & (px < W) & (py >= 0) & (py < H)
px, py, gmag = px[on], py[on], gmag[on]
ix, iy = np.clip(np.round(px).astype(int), 0, W - 1), np.clip(np.round(py).astype(int), 0, H - 1)
rms = fits.getdata(work / f"{inp.stem}_c1.rms.fits").astype(np.float64)
yy, xx = np.mgrid[-24:25, -24:25]
rr = np.hypot(xx, yy)
disc, ann = rr < 3, (rr >= 16) & (rr < 24)
keep = (ix >= 24) & (ix < W - 24) & (iy >= 24) & (iy < H - 24)

B = 32
noise = 1.4826 * float(np.median(np.abs(np.diff(g[::3], axis=1)))) / np.sqrt(2)
bh, bw = H // B, W // B
lvl = np.median(g[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
lvl = (lvl - np.percentile(lvl, 5)) / noise

sel = np.where(keep & (gmag >= 17) & (gmag < 19))[0]
if len(sel) > 40000:
    sel = np.random.default_rng(1).choice(sel, 40000, replace=False)
seen, light, level = [], [], []
for k in sel:
    x, y = ix[k], iy[k]
    win = g[y - 24:y + 25, x - 24:x + 25]
    excess = (win[disc] - np.median(win[ann])).sum()
    if excess > 3 * rms[y, x] * np.sqrt(disc.sum()):
        seen.append(k)
        light.append(excess / noise)
        level.append(lvl[min(y // B, bh - 1), min(x // B, bw - 1)])
seen, light, level = np.array(seen), np.array(light), np.array(level)
print(f"{inp.name}: BP 17 to 19, {len(sel)} drawn, {len(seen)} seen; sky noise {noise:.3e}; "
      f"their level: median {np.median(level):.0f} sky sigma, {100 * np.mean(level < 5):.0f} % under 5, "
      f"{100 * np.mean(level >= 20):.0f} % at 20 or over; their light: median {np.median(light):.0f} sky sigma")
# The nearest other catalogued star (to BP 19), and the nearest BRIGHTER one: a blend is a star with one close by.
tree = cKDTree(np.column_stack([px, py]))
near, near_brighter = np.empty(len(seen)), np.empty(len(seen))
for j, k in enumerate(seen):
    d, i = tree.query((px[k], py[k]), k=min(40, len(px)))
    near[j] = d[1]
    b = [dd for dd, ii in zip(d[1:], i[1:]) if gmag[ii] < gmag[k]]
    near_brighter[j] = b[0] if b else 99.0
print(f"nearest other catalogued star: median {np.median(near):.1f} px; {100 * np.mean(near < 4):.0f} % within 4 px, "
      f"{100 * np.mean(near < 8):.0f} % within 8; a brighter one within 8 px: {100 * np.mean(near_brighter < 8):.0f} %")
NEARS = [(0, 3), (3, 5), (5, 8), (8, 12), (12, 1e9)]
# How crowded the star's place is: catalogued stars (to BP 19) within 16 px of it, itself not counted.
crowd = np.array([len(tree.query_ball_point((px[k], py[k]), 16.0)) - 1 for k in seen])
CQ = np.percentile(crowd, [33.3, 66.7])
print(f"catalogued stars within 16 px: median {np.median(crowd):.0f}, thirds at {CQ[0]:.0f} and {CQ[1]:.0f}; "
      f"correlation of that count with the level {np.corrcoef(crowd, np.clip(level, -5, 60))[0, 1]:.2f}")
LEVELS = [(-1e9, 5), (5, 20), (20, 40), (40, 80), (80, 1e9)]
LIGHTS = [(0, 20), (20, 50), (50, 150), (150, 1e9)]


def name(lo, hi, unit):
    return f"under {hi:g}" if lo < 0 or lo == 0 else (f"{lo:g} and over" if hi > 1e8 else f"{lo:g} to {hi:g}")


def cell(sel_, aside):
    return f"{int(sel_.sum()):6d} {100 * np.mean(aside[sel_] > 0.5):5.1f} %" if sel_.sum() >= 20 else f"{int(sel_.sum()):6d}     -  "


shares = {}
for label, path in removers:
    st = fits.getdata(path).astype(np.float64)
    st = st[1] if st.ndim == 3 else st
    taken = g - st
    aside = np.empty(len(seen))
    for j, k in enumerate(seen):
        x, y = ix[k], iy[k]
        t = taken[y - 24:y + 25, x - 24:x + 25]
        aside[j] = (t[disc] - np.median(t[ann])).sum() / (light[j] * noise)
    shares[label] = aside
    print(f"\n{label}: removed over half, level set aside: all {100 * np.mean(aside > 0.5):.1f} % of {len(aside)}")
    print("  by the nebula under the star (sky sigma):    stars  removed")
    for lo, hi in LEVELS:
        print(f"    {name(lo, hi, ''):>14s}                           {cell((level >= lo) & (level < hi), aside)}")
    print("  by the star's light (sky sigma):             stars  removed")
    for lo, hi in LIGHTS:
        print(f"    {name(lo, hi, ''):>14s}                           {cell((light >= lo) & (light < hi), aside)}")
    print("  stars of 20 to 150 sky sigma, by the nebula under them:")
    for lo, hi in LEVELS:
        print(f"    {name(lo, hi, ''):>14s}                           {cell((level >= lo) & (level < hi) & (light >= 20) & (light < 150), aside)}")
    print("  by the nearest other catalogued star (px):   stars  removed     and by the nearest BRIGHTER one:  stars  removed")
    for lo, hi in NEARS:
        print(f"    {name(lo, hi, ''):>14s}                           {cell((near >= lo) & (near < hi), aside)}"
              f"                                    {cell((near_brighter >= lo) & (near_brighter < hi), aside)}")
    print("  under 20 sky sigma of nebula, by the nearest other catalogued star (px):")
    for lo, hi in NEARS:
        print(f"    {name(lo, hi, ''):>14s}                           {cell((near >= lo) & (near < hi) & (level < 20), aside)}")
    print("  stars of 20 to 150 sky sigma: the level under the star (rows) by how crowded its place is (thirds of the count within 16 px):")
    print("                          least crowded         middle                most crowded")
    faint = (light >= 20) & (light < 150)
    for lo, hi in ((-1e9, 5), (5, 10), (10, 20), (20, 1e9)):
        cells = [cell(faint & (level >= lo) & (level < hi) & c, aside)
                 for c in (crowd <= CQ[0], (crowd > CQ[0]) & (crowd <= CQ[1]), crowd > CQ[1])]
        print(f"    {name(lo, hi, ''):>14s}      {cells[0]}        {cells[1]}        {cells[2]}")

labels = [l for l, _ in removers]
if len(labels) >= 2:
    a, b = shares[labels[0]] > 0.5, shares[labels[1]] > 0.5
    only = a & ~b
    print(f"\nstars {labels[0]} removes and {labels[1]} leaves: {int(only.sum())} of {len(a)} "
          f"({100 * only.mean():.1f} %); the other way {int((b & ~a).sum())}; both leave {int((~a & ~b).sum())}")
    if only.sum() >= 20:
        print(f"  of those {int(only.sum())}: level median {np.median(level[only]):.0f} sky sigma "
              f"({100 * np.mean(level[only] >= 20):.0f} % at 20 or over, against {100 * np.mean(level >= 20):.0f} % of all); "
              f"light median {np.median(light[only]):.0f} sky sigma (all: {np.median(light):.0f}); "
              f"nearest other star median {np.median(near[only]):.1f} px (all: {np.median(near):.1f}), "
              f"{100 * np.mean(near[only] < 5):.0f} % within 5 px (all: {100 * np.mean(near < 5):.0f} %), "
              f"a brighter one within 8 px {100 * np.mean(near_brighter[only] < 8):.0f} % "
              f"(all: {100 * np.mean(near_brighter < 8):.0f} %)")
