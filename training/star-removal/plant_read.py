"""The planted-star test, step 2 (2026-11-23): how much of each planted star a remover took, by where it was planted.

    python -I plant_read.py <master> <the remover's plate of the master> <plant folder> <label> <seed>=<its plate of planted-<seed>.fits> ...

T is the planted light (planted master less master, exact). What the remover took of it is T less the change of its
plate (its plate of the planted master less its plate of the master), so the field's own stars and the nebula cancel.
Both plates must come from one build with the same options (no core protection, no dig fill, --keep-overshoot: the remover as it is).
Per plant, as nebula_stars_row.py reads a star: the taken light in a 2 px disc less its ring median 5 to 9 px out,
over T's own. Printed: the median share taken and the share of plants LEFT (under half taken), bright nebula beside
dark sky:
  - by the light planted, in sky sigma (the figure to quote);
  - by that light in the spot's LOCAL noise (a nebula's shot noise is higher, so a star of one light stands out less);
  - bright plants by the nebula's level under them;
  - dark plants by the light as it APPEARS on the planted master (the planted light plus the spot's own noise): that is
    how a real star is selected, so this is the line to set beside nebula_stars_row.py's real stars on dark sky.
The last line is one a scorer can parse:

    planted <label> bright n <n> left <share> % | dark n <n> left <share> %      (plants of 20 to 150 sky sigma)
"""
import csv
import os
import sys

import numpy as np
from astropy.io import fits

master, plate0, folder, label = sys.argv[1:5]
runs = [s.split("=", 1) for s in sys.argv[5:]]


def load(path):
    x = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return x[:3].mean(axis=0) if x.ndim == 3 else x


m, s0 = load(master), load(plate0)
noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
yy, xx = np.mgrid[-9:10, -9:10]
rr2 = xx ** 2 + yy ** 2
disc, ring = rr2 <= 4, (rr2 >= 25) & (rr2 <= 81)


def row_light(q):
    return float((q - np.median(q[ring]))[disc].sum())


rows = []
for seed, plate in runs:
    pm, sp = load(os.path.join(folder, f"planted-{seed}.fits")), load(plate)
    t = pm - m
    took = t - (sp - s0)
    for r in csv.DictReader(open(os.path.join(folder, f"plants-{seed}.tsv"), encoding="utf-8"), delimiter="\t"):
        x, y = int(r["x"]), int(r["y"])
        box = (slice(y - 9, y + 10), slice(x - 9, x + 10))
        rows.append((r["kind"], float(r["light"]), float(r["light"]) / float(r["local_noise"]), float(r["level"]),
                     row_light(took[box]) / row_light(t[box]), int(seed), row_light(pm[box]) / noise))
kind = np.array([r[0] for r in rows])
light, local, level, taken, seed, appears = (np.array([r[i] for r in rows], float) for i in (1, 2, 3, 4, 5, 6))
bright, dark = kind == "bright", kind == "dark"


def part(sel):
    if sel.sum() < 5:
        return f"n {int(sel.sum()):3d} taken    - left     -  "
    return f"n {int(sel.sum()):3d} taken {np.median(taken[sel]):5.2f} left {(taken[sel] < 0.5).mean() * 100:5.1f} %"


def left(sel):
    return "-" if sel.sum() < 5 else f"{(taken[sel] < 0.5).mean() * 100:.1f} %"


print(f"{label}: {len(rows)} plants over seeds {sorted(set(int(s) for s in seed))}")
print(f"  all                         bright {part(bright)} | dark {part(dark)}")
for s in sorted(set(seed)):
    print(f"  seed {int(s)}                      bright {part(bright & (seed == s))} | dark {part(dark & (seed == s))}")
print("  by the light planted, sky sigma:")
for lo, hi in ((7, 20), (20, 50), (50, 150)):
    b = (light >= lo) & (light < hi)
    print(f"    {lo:3d} to {hi:3d}                bright {part(b & bright)} | dark {part(b & dark)}")
print("  by the light planted in the spot's LOCAL noise:")
for lo, hi in ((2, 7), (7, 20), (20, 50), (50, 150)):
    b = (local >= lo) & (local < hi)
    print(f"    {lo:3d} to {hi:3d}                bright {part(b & bright)} | dark {part(b & dark)}")
print("  bright plants of 20 to 150 sky sigma by the nebula's level under them, sky sigma:")
for lo, hi in ((20, 40), (40, 80), (80, 1e9)):
    b = bright & (level >= lo) & (level < hi) & (light >= 20)
    print(f"    {lo:3d} to {'more' if hi > 1e8 else int(hi):4}              {part(b)}")
print("  dark plants by the light as it APPEARS on the planted master (as a real star is selected):")
for lo, hi in ((7, 20), (20, 50), (50, 150)):
    print(f"    {lo:3d} to {hi:3d}                {part(dark & (appears >= lo) & (appears < hi))}")
b = (light >= 20) & (light < 150)
print(f"planted {label} bright n {int((b & bright).sum())} left {left(b & bright)} | dark n {int((b & dark).sum())} left {left(b & dark)}")
