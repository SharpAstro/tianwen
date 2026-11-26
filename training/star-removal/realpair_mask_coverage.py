"""How many of a real-pairs export's catalogued faint stars on bright nebula its loss masks cover (2026-11-26; R4k's
read and the training target's own scorecard left this uncounted, docs/plans/star-remover-training.md).

    python -I realpair_mask_coverage.py <export root> <bake> <gaia dir> <sessions file> [--match 2]

R4k trained on a real-pairs export (`tianwen dataset degrade --mode stars --background real`): each cell is a master's
own 256 px tile against its starless plate's, and the loss counts only where `<tile>.keep.f16` is 1 (RealPairMask: the
builder's subtracted footprint, less 2 PSF widths round every source it left in and 30 px round every saturated star).
This reads, for every session named in <sessions file> (one session id a line, as the trainer's lists hold them):

- the stars: the session master's catalogue (<gaia dir>/<stem>.csv, `tianwen solve --export-gaia`'s x, y, ..., mag),
  selected exactly as nebula_stars_row.py selects them (no brighter catalogued star within 6 px; 20 to 150 sky sigma
  of light in a 2 px disc over its 5 to 9 px annulus; the magnitude check at --match; bright nebula is a 32 px block's
  median 20 sky sigma or more over the frame's 5th percentile block, dark sky under 5);
- per star inside an exported cell (--edge px clear of its edge, default 3; the trainer's loss skips a 16 px rim, --edge 16): whether the mask covers it (the mean of the keep plane over
  its 2 px disc is a half or more), and the share of its light the stored plate took (nebula_stars_row.py's "taken").

Printed per session with five or more such stars on bright nebula, and summed over all sessions read ("all") and over
the sessions whose magnitude check could run ("checked"; with under 50 dark-sky stars it is off and every catalogued
position counts, a bump of the nebula among them), each sum also split by the nebula's level as plant_read.py splits it:

    maskcover <stem, all or checked> bright n <n> covered <share> % taken <share> % | dark n <n> covered <share> % taken <share> %
    maskcover <all or checked> level <lo> to <hi> n <n> covered <share> % taken <share> %

"taken" is the share of stars of which the plate took half or more of the light (nebula_stars_row.py's "left" is
100 less it). A session whose master, plate, catalogue or cells are missing is named on stderr and skipped. Reads only.
"""
import argparse
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.spatial import cKDTree

BRIGHT, DARK, B = 20.0, 5.0, 32
FIT_LIGHT, FIT_MIN = (30.0, 1500.0), 50

p = argparse.ArgumentParser()
p.add_argument("export")
p.add_argument("bake")
p.add_argument("gaia_dir")
p.add_argument("sessions")
p.add_argument("--match", type=float, default=2.0)
p.add_argument("--edge", type=int, default=3, help="px a star must stand clear of its cell's edge (the trainer's loss skips a 16 px rim: --edge 16)")
a = p.parse_args()
EDGE = a.edge
root, bake, gdir = Path(a.export), Path(a.bake), Path(a.gaia_dir)
wanted = {line.strip() for line in open(a.sessions, encoding="utf-8") if line.strip()}

cells = defaultdict(list)   # session id -> [(stem, cell x, cell y, keep path)]
for line in open(root / "degradations.jsonl", encoding="utf-8"):
    r = json.loads(line)
    if r["SessionId"] in wanted and r["Draw"] == 0:
        tile = root / r["Tile"]
        cells[r["SessionId"]].append((tile.parent.name, r["CellX"], r["CellY"], r.get("TileSize", 256), tile.with_name(tile.stem + ".keep.f16")))


def lum(path):
    x = np.nan_to_num(np.asarray(fits.getdata(path), np.float64))
    return x[:3].mean(axis=0) if x.ndim == 3 else x


yy, xx = np.mgrid[-2:3, -2:3]
disc = (xx ** 2 + yy ** 2) <= 4
ay, ax = np.mgrid[-9:10, -9:10]
ring = ((ax ** 2 + ay ** 2) >= 25) & ((ax ** 2 + ay ** 2) <= 81)


def session(stem, its_cells):
    master, plate, gaia = bake / "session-masters" / f"{stem}.fits", bake / "starless" / "plates" / f"{stem}_plate.fits", gdir / f"{stem}.csv"
    for f in (master, plate, gaia):
        if not f.exists():
            print(f"skipped {stem}: no {f.name}", file=sys.stderr)
            return None
    m, pl = lum(master), lum(plate)
    if m.shape != pl.shape:
        print(f"skipped {stem}: plate {pl.shape} against master {m.shape}", file=sys.stderr)
        return None
    H, W = m.shape
    noise = 1.4826 * float(np.median(np.abs(np.diff(m[::3], axis=1)))) / np.sqrt(2)
    bh, bw = H // B, W // B
    lvl = np.median(m[:bh * B, :bw * B].reshape(bh, B, bw, B).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
    lvl = (lvl - np.percentile(lvl, 5)) / noise
    rows = list(csv.DictReader(open(gaia, encoding="utf-8")))
    if not rows:
        print(f"skipped {stem}: empty catalogue", file=sys.stderr)
        return None
    cat = np.array([(float(r["x"]), float(r["y"]), float(r["mag"])) for r in rows])
    tree = cKDTree(cat[:, :2])
    keep = {}
    for _, cx, cy, size, kp in its_cells:
        if not kp.exists():
            print(f"{stem}: no {kp.name} for the cell at {cx}, {cy}; its stars are not counted", file=sys.stderr)
            continue
        k = np.frombuffer(kp.read_bytes(), np.float16).astype(np.float32)
        if k.size != size * size:
            print(f"{stem}: {kp.name} holds {k.size} values, not {size} x {size}; skipped", file=sys.stderr)
            continue
        keep[(cx, cy)] = (size, k.reshape(size, size))
    stars_layer = m - pl
    out = []   # (mag, light, level, taken, covered or None)
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
        taken = float((ps[7:12, 7:12] - np.median(ps[ring]))[disc].sum()) / light
        covered = None
        for (cx, cy), (size, k) in keep.items():
            if cx + EDGE <= x < cx + size - EDGE and cy + EDGE <= y < cy + size - EDGE:
                covered = float(k[y - cy - 2:y - cy + 3, x - cx - 2:x - cx + 3][disc].mean()) >= 0.5
                break
        out.append((mag, light / noise, float(lvl[y // B, x // B]), taken, covered))
    if not out:
        return None
    s = np.array([(o[0], o[1], o[2], o[3], -1 if o[4] is None else int(o[4])) for o in out])
    mags, lights, level, taken, cov = s[:, 0], s[:, 1], s[:, 2], s[:, 3], s[:, 4]
    sel = (lights >= 20) & (lights < 150) & (cov >= 0)
    checked = False
    if a.match > 0:
        fit = (level < DARK) & (lights >= FIT_LIGHT[0]) & (lights < FIT_LIGHT[1])
        if fit.sum() >= FIT_MIN:
            zero = float(np.median(mags[fit] + 2.5 * np.log10(lights[fit])))
            ratio = lights / 10 ** (-0.4 * (mags - zero))
            sel &= (ratio >= 1 / a.match) & (ratio <= a.match)
            checked = True
        else:
            print(f"{stem}: {int(fit.sum())} dark-sky stars to fit the zero point on; the magnitude check is off", file=sys.stderr)
    return [(level[i], taken[i], bool(cov[i]), checked) for i in np.flatnonzero(sel)]


def part(rows):
    if len(rows) < 5:
        return f"n {len(rows)} covered - taken -"
    cov = np.array([r[2] for r in rows])
    tk = np.array([r[1] for r in rows])
    return f"n {len(rows)} covered {cov.mean() * 100:.1f} % taken {(tk >= 0.5).mean() * 100:.1f} %"


every = []
for sid in sorted(cells):
    stem = cells[sid][0][0]
    got = session(stem, cells[sid])
    if got is None:
        continue
    every += got
    bright = [r for r in got if r[0] >= BRIGHT]
    if len(bright) >= 5:
        print(f"maskcover {stem} bright {part(bright)} | dark {part([r for r in got if r[0] < DARK])}", flush=True)
missing = sorted(wanted - set(cells))
if missing:
    print(f"{len(missing)} named sessions have no cells in the export", file=sys.stderr)


def summary(label, rows):
    bright = [r for r in rows if r[0] >= BRIGHT]
    print(f"maskcover {label} bright {part(bright)} | dark {part([r for r in rows if r[0] < DARK])}")
    for lo, hi in ((20, 40), (40, 80), (80, 1e9)):
        print(f"maskcover {label} level {lo} to {'more' if hi > 1e8 else hi} {part([r for r in bright if lo <= r[0] < hi])}")
    if bright:
        tk = np.array([r[1] for r in bright]) >= 0.5
        cov = np.array([r[2] for r in bright])
        print(f"maskcover {label} bright: of those taken, covered {cov[tk].mean() * 100 if tk.any() else float('nan'):.1f} %; "
              f"of those left, covered {cov[~tk].mean() * 100 if (~tk).any() else float('nan'):.1f} %")


summary("all", every)
summary("checked", [r for r in every if r[3]])
