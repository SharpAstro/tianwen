"""The 9 to 41 px band of an export's target tiles, the statistic the plan reads the crowded grain with (band.py's:
through a 7 px median, the 9 px box mean less the 41 px one, rms in sigma of a 9 px mean), from two exports
(#2203, R4f, 2026-11-20).
    python -I tile_band.py <export A> <export B> <session substring>
Target tiles with a block of at least 400 exact zeros in either export (the knot defect, #2276) are left out. The rms is
over the tile's inner 176 px, clear of the 41 px box's reach to the edge."""
import json, os, sys
import numpy as np
from scipy.ndimage import median_filter, uniform_filter

A, B, KEY = sys.argv[1], sys.argv[2], sys.argv[3].lower()
T = 256


def load(path):
    return np.fromfile(path, "<f2").astype(np.float32).reshape(-1, T, T)


def band(t):
    out = []
    for c in t:
        sig = 1.4826 * np.median(np.abs(np.diff(c, axis=1))) / np.sqrt(2)
        m = median_filter(c, 7)
        b = uniform_filter(m, 9) - uniform_filter(m, 41)
        out.append(float(np.sqrt(np.mean(b[40:-40, 40:-40] ** 2)) / max(sig / 9.0, 1e-12)))
    return np.array(out)


blocked = 0; ba, bb, cells = [], [], []
for line in open(os.path.join(A, "tiles-manifest.jsonl"), encoding="utf-8"):
    r = json.loads(line)
    if r["Frame"] != "master" or KEY not in r["SessionId"].lower() or "flip=" in r["SessionId"]:
        continue
    a, b = load(os.path.join(A, r["Tile"])), load(os.path.join(B, r["Tile"]))
    if (a == 0).all(axis=0).sum() >= 400 or (b == 0).all(axis=0).sum() >= 400:
        blocked += 1
        continue
    ba.append(band(a)); bb.append(band(b)); cells.append((r["CellX"], r["CellY"]))
ba, bb = np.array(ba), np.array(bb)
f = lambda v: " / ".join(f"{x:.1f}" for x in v)
print(f"left out for a zero block: {blocked}; read: {len(ba)}")
print(f"band, median over the tiles: A {f(np.median(ba, axis=0))}; B {f(np.median(bb, axis=0))}")
print(f"band, 90th percentile: A {f(np.percentile(ba, 90, axis=0))}; B {f(np.percentile(bb, 90, axis=0))}")
ratio = bb / np.maximum(ba, 1e-9)
print(f"B over A, per tile: median {' / '.join(f'{x:.2f}' for x in np.median(ratio, axis=0))}; lowest {' / '.join(f'{x:.2f}' for x in ratio.min(axis=0))}; highest {' / '.join(f'{x:.2f}' for x in ratio.max(axis=0))}")
order = np.argsort(ratio.mean(axis=1))
for i in list(order[:4]) + list(order[-2:]):
    print(f"   cell x{cells[i][0]} y{cells[i][1]}: A {f(ba[i])}  B {f(bb[i])}")
