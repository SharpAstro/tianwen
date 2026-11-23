"""The scorecard's row for faint stars on bright nebula (2026-11-22, the owner: '"+" stars in lets say lagoon ... SXT
seems to be able to deal with them but we struggle').

    python -I nebula_stars_row.py <master> <gaia.csv> <starless plate> --name <label> [--light 20,150 | --bp 13.5,15.5]
                                  [--match 2]

Catalogued stars (the catalogue: `tianwen solve --export-gaia`'s CSV of the master, whose magnitude is Gaia's BP) with no
brighter catalogued star within 6 px, whose light in a 2 px disc over the annulus 5 to 9 px out is 20 to 150 sky sigma
(the faint ones in the data's terms, the same on every master; --bp selects by magnitude instead), on bright nebula
(their 32 px block's median 20 sky sigma or more over the frame's sky, the blocks' 5th percentile) and, as the control,
on dark sky (under 5). Per star, the share of
its light the plate took: the stars layer (master less plate) summed over a 2 px disc less its own annulus median 5 to
9 px out, over the master's disc less its annulus median, so neither a broad level the plate took nor the nebula's
structure, which master and plate share, counts. Printed, in a fixed wording a scorer parses:

    nebstars <label> bright n <n> taken <median> left <share> % | dark n <n> taken <median> left <share> %

"left" is the share of stars of which the plate took under half, and it is the figure to quote: a star is taken whole
or not at all, so the median flips between about 0.1 and 0.5 where "left" passes one half (two seeds of one recipe
read 0.10 and 0.53 on the Lagoon). A master with fewer than 5 such stars on bright
nebula prints "bright n <n> taken - left -". The sky noise is the master's (MAD of horizontal neighbour differences).

The magnitude check (--match, 2026-11-23). A catalogue lists a star every few pixels on a Milky Way field, most of them
far under the noise, and on bright nebula a bump of the nebula or of its noise at such a place reads as 20 sky sigma of
"light": of the Lagoon's 180 stars in the row as first built, 96 held over twice (or under half) the light their
magnitude gives, and a remover is right to leave a bump. So a star counts only when its measured light is within a
factor --match of what its magnitude gives ON THIS MASTER: the zero point is the median of mag + 2.5 log10(light in sky
sigma) over the master's own dark-sky stars of 30 to 1500 sky sigma. --match 0 turns the check off and gives the row
as it read on 2026-11-22. With under 50 dark-sky stars to fit, the check is off and a line on stderr says so.
"""
import argparse
import csv
import sys

import numpy as np
from astropy.io import fits
from scipy.spatial import cKDTree

p = argparse.ArgumentParser()
p.add_argument("master")
p.add_argument("gaia")
p.add_argument("plate")
p.add_argument("--name", required=True)
p.add_argument("--light", default="20,150", help="lo,hi: the star's disc light in sky sigma (the default selection)")
p.add_argument("--bp", default=None, help="lo,hi: select by Gaia BP instead of light")
p.add_argument("--match", type=float, default=2.0,
               help="a star counts when its measured light is within this factor of what its magnitude gives on this "
                    "master; 0 turns the check off (the row as it read on 2026-11-22)")
a = p.parse_args()
LIGHT = None if a.bp else tuple(float(v) for v in a.light.split(","))
LO, HI = tuple(float(v) for v in a.bp.split(",")) if a.bp else (-99.0, 99.0)
BRIGHT, DARK, B = 20.0, 5.0, 32
FIT_LIGHT, FIT_MIN = (30.0, 1500.0), 50


def lum(path):
    x = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return x[:3].mean(axis=0) if x.ndim == 3 else x


m, plate = lum(a.master), lum(a.plate)
if m.shape != plate.shape:
    raise SystemExit(f"plate {plate.shape} against master {m.shape}")
H, W = m.shape
noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
bh, bw = H // B, W // B
lvl = np.median(m[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
lvl = (lvl - np.percentile(lvl, 5)) / noise
cat = np.array([(float(r["x"]), float(r["y"]), float(r["mag"])) for r in csv.DictReader(open(a.gaia, encoding="utf-8"))])
tree = cKDTree(cat[:, :2])
yy, xx = np.mgrid[-2:3, -2:3]
disc = (xx ** 2 + yy ** 2) <= 4
ay, ax = np.mgrid[-9:10, -9:10]
ring = ((ax ** 2 + ay ** 2) >= 25) & ((ax ** 2 + ay ** 2) <= 81)
stars_layer = m - plate
mags, lights, taken, level = [], [], [], []
for fx, fy, mag in cat:
    x, y = int(round(fx)), int(round(fy))
    if not (12 <= x < bw * B - 12 and 12 <= y < bh * B - 12):
        continue
    if any(cat[j, 2] < mag for j in tree.query_ball_point((fx, fy), 6.0)):
        continue
    pm, ps = m[y - 9:y + 10, x - 9:x + 10], stars_layer[y - 9:y + 10, x - 9:x + 10]
    light = float((pm[7:12, 7:12] - np.median(pm[ring]))[disc].sum())
    if light <= 0:
        continue
    mags.append(mag)
    lights.append(light / noise)
    taken.append(float((ps[7:12, 7:12] - np.median(ps[ring]))[disc].sum()) / light)
    level.append(float(lvl[y // B, x // B]))
mags, lights, taken, level = np.array(mags), np.array(lights), np.array(taken), np.array(level)

sel = (mags >= LO) & (mags < HI)
if LIGHT is not None:
    sel &= (lights >= LIGHT[0]) & (lights < LIGHT[1])
if a.match > 0:
    fit = (level < DARK) & (lights >= FIT_LIGHT[0]) & (lights < FIT_LIGHT[1])
    if fit.sum() < FIT_MIN:
        print(f"nebstars {a.name}: {int(fit.sum())} dark-sky stars to fit the zero point on, under {FIT_MIN}: "
              "the magnitude check is off", file=sys.stderr)
    else:
        colour = mags[fit] + 2.5 * np.log10(lights[fit])
        zero = float(np.median(colour))
        ratio = lights / 10 ** (-0.4 * (mags - zero))
        matched = (ratio >= 1 / a.match) & (ratio <= a.match)
        print(f"nebstars {a.name}: zero point {zero:.2f} from {int(fit.sum())} dark-sky stars (scatter "
              f"{1.4826 * float(np.median(np.abs(colour - zero))):.2f} mag); the magnitude check keeps "
              f"{int((sel & matched & (level >= BRIGHT)).sum())} of {int((sel & (level >= BRIGHT)).sum())} on bright nebula "
              f"and {int((sel & matched & (level < DARK)).sum())} of {int((sel & (level < DARK)).sum())} on dark sky",
              file=sys.stderr)
        sel &= matched


def part(where):
    s = sel & where
    if s.sum() < 5:
        return f"n {int(s.sum())} taken - left -"
    return f"n {int(s.sum())} taken {np.median(taken[s]):.2f} left {(taken[s] < 0.5).mean() * 100:.1f} %"


print(f"nebstars {a.name} bright {part(level >= BRIGHT)} | dark {part(level < DARK)}")
