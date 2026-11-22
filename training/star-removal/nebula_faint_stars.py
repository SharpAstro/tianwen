"""Catalogued faint stars on bright nebula against the same stars on dark sky: how much of each star each remover takes,
the bump it leaves, and the light the model SEES (2026-11-22, the owner's '"+" stars in the Lagoon').

    python -I nebula_faint_stars.py <master> <gaia.csv> <sxt starless> <ours starless>

The catalogue is `tianwen solve --export-gaia`'s CSV of the master (x, y, mag). The level under a star is its 32 px block's
median over the frame's sky (the blocks' 5th percentile), in the sky noise; bright nebula is 20 sky sigma and over. Stars
with a brighter catalogued neighbour within 6 px are left out. Per star: the light in a 2 px disc over the annulus median
5 to 9 px out; the share of it each remover took; the bump each starless keeps (its 3x3 mean over its own annulus median,
in sky noise). Three tables:
  1. by Gaia magnitude and region: share taken and bump left, SXT / ours;
  2. ours less SXT at the bright nebula's stars (3x3 over the annulus, sky noise; the nebula's structure is in both plates
     and cancels), against random spots of the same nebula with no catalogued star within 6 px;
  3. by the light the model sees: the export's stretch (glow_ring.stretch_params: master over its peak, less the
     channel's minimum, the MTF taking the median to 0.25), the stretched disc light in the stretched sky noise; at equal
     light, the share taken on dark sky and on bright nebula.
"""
import csv
import sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.spatial import cKDTree

sys.path.insert(0, str(Path(__file__).resolve().parent))
from glow_ring import stretch, stretch_params  # noqa: E402

MASTER, CATALOGUE, SXT, OURS = sys.argv[1:5]


def lum(path):
    a = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return a[:3].mean(axis=0) if a.ndim == 3 else a


m, sxt, ours = lum(MASTER), lum(SXT), lum(OURS)
H, W = m.shape
noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
div, mins, betas, _ = stretch_params(m[None])
s = stretch(m[None], div, mins, betas)[0]
snoise = 1.4826 * float(np.median(np.abs(np.diff(s[::3], axis=1)))) / np.sqrt(2)
B = 32
bh, bw = H // B, W // B


def blocks(a):
    return np.median(a[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)


lvl = blocks(m)
lvl = (lvl - np.percentile(lvl, 5)) / noise
slvl = blocks(s)
bright = lvl >= 20
bm = np.repeat(np.repeat(bright, B, 0), B, 1)
hd = np.zeros_like(m, dtype=bool)
hd[:bh * B, :bw * B - 1] = bm[:, :-1] & bm[:, 1:]
bnoise = 1.4826 * float(np.median(np.abs(np.diff(m, axis=1)[:, :bw * B - 1][:bh * B][hd[:bh * B, :bw * B - 1]]))) / np.sqrt(2)
print(f"sky noise {noise:.3g}; bright nebula: {bright.sum()} blocks of {B} px, its pixel noise {bnoise / noise:.2f} times the "
      f"sky's; the export's stretch puts the sky at {np.median(slvl):.3f} and the bright nebula at {np.median(slvl[bright]):.3f}")
cat = np.array([(float(r["x"]), float(r["y"]), float(r["mag"])) for r in csv.DictReader(open(CATALOGUE, encoding="utf-8"))])
tree = cKDTree(cat[:, :2])
yy, xx = np.mgrid[-2:3, -2:3]
disc = (xx ** 2 + yy ** 2) <= 4
ay, ax = np.mgrid[-9:10, -9:10]
ring = ((ax ** 2 + ay ** 2) >= 25) & ((ax ** 2 + ay ** 2) <= 81)
d = ours - sxt


def lift(y, x):
    p = d[y - 9:y + 10, x - 9:x + 10]
    return (float(p[8:11, 8:11].mean()) - float(np.median(p[ring]))) / noise


rows = []
for fx, fy, mag in cat:
    x, y = int(round(fx)), int(round(fy))
    if not (12 <= x < bw * B - 12 and 12 <= y < bh * B - 12):
        continue
    if any(cat[j, 2] < mag for j in tree.query_ball_point((fx, fy), 6.0)):
        continue
    pm, ps, psx, po = (a[y - 9:y + 10, x - 9:x + 10] for a in (m, s, sxt, ours))
    light = float((pm[7:12, 7:12] - np.median(pm[ring]))[disc].sum())
    if light <= 0:
        continue
    slight = float((ps[7:12, 7:12] - np.median(ps[ring]))[disc].sum()) / snoise
    out = [mag, float(lvl[y // B, x // B]), light / noise, slight]
    for p in (psx, po):
        out.append(float((pm[7:12, 7:12] - p[7:12, 7:12])[disc].sum()) / light)
        out.append((float(p[8:11, 8:11].mean()) - float(np.median(p[ring]))) / noise)
    out.append(lift(y, x))
    rows.append(out)
a = np.array(rows)
dark, neb = a[:, 1] < 5, a[:, 1] >= 20
print(f"{len(a)} catalogued stars with positive disc light; dark sky (level under 5) {dark.sum()}, bright nebula {neb.sum()}")
print("\n1. by Gaia magnitude: share taken SXT / ours, bump left SXT / ours (sky sigma), bumps over 3 sigma SXT / ours")
for lo, hi in ((12, 14), (14, 15), (15, 16), (16, 17), (17, 18)):
    for name, sel in (("dark sky", dark), ("bright nebula", neb)):
        t = sel & (a[:, 0] >= lo) & (a[:, 0] < hi)
        if t.sum() >= 5:
            print(f"  G {lo} to {hi} {name:13s} {t.sum():6d}: taken {np.median(a[t, 4]):5.2f} / {np.median(a[t, 6]):5.2f}; bump "
                  f"{np.median(a[t, 5]):5.2f} / {np.median(a[t, 7]):5.2f}; over 3 sigma {(a[t, 5] > 3).mean() * 100:3.0f} % / "
                  f"{(a[t, 7] > 3).mean() * 100:3.0f} %")
rng = np.random.default_rng(0)
mask = np.repeat(np.repeat(bright, B, 0), B, 1)
ys, xs = np.nonzero(mask)
pick = rng.choice(len(ys), min(20000, len(ys)), replace=False)
null = np.array([lift(y, x) for y, x in zip(ys[pick], xs[pick]) if 12 <= x < bw * B - 12 and 12 <= y < bh * B - 12
                 and not tree.query_ball_point((float(x), float(y)), 6.0)])
print(f"\n2. ours less SXT at the bright nebula's stars; null, {len(null)} spots with no star within 6 px: over +3 sigma "
      f"{(null > 3).mean() * 100:.1f} %, under -3 {(null < -3).mean() * 100:.1f} %")
for lo, hi in ((10, 14), (14, 15), (15, 16), (16, 17), (17, 18)):
    t = neb & (a[:, 0] >= lo) & (a[:, 0] < hi)
    if t.sum() >= 5:
        print(f"  G {lo} to {hi}: {t.sum():4d} stars; ours brighter by over 3 sigma on {(a[t, 8] > 3).mean() * 100:3.0f} %, "
              f"darker by over 3 on {(a[t, 8] < -3).mean() * 100:3.0f} %")
print("\n3. by the light the model sees (stretched disc light, stretched sky sigma): share taken SXT / ours")
for lo, hi in ((4, 8), (8, 16), (16, 32), (32, 64), (64, 128), (128, 1e9)):
    cells = []
    for name, sel in (("dark sky", dark), ("bright nebula", neb)):
        t = sel & (a[:, 3] >= lo) & (a[:, 3] < hi)
        cells.append(f"{name} {t.sum():5d}: {np.median(a[t, 4]):5.2f} / {np.median(a[t, 6]):5.2f}" if t.sum() >= 5 else f"{name} {t.sum():5d}: -")
    print(f"  {lo:>4g} to {hi:<6g} " + "   |   ".join(cells))
