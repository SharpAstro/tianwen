"""The grain rule's effect on a session's target tiles with the knot defect's zero blocks set aside (#2276, a correction to
R4f's registration, 2026-11-20).
    python -I tile_grain_clean.py <export from the stored plates> <export from the grain plates> <session substring>
Target tiles holding a block of at least 400 exact zeros in EITHER export are counted and left out; over the rest, the
texture between a 3 px and a 31 px box in the tile's pixel noise (as tile_grain.py), stored against grain: median, 90th
percentile and the per-tile ratio."""
import json, os, sys
import numpy as np
from scipy.ndimage import uniform_filter

A, B, KEY = sys.argv[1], sys.argv[2], sys.argv[3].lower()
T = 256


def load(path):
    return np.fromfile(path, "<f2").astype(np.float32).reshape(-1, T, T)


def texture(t):
    out = []
    for c in t:
        sig = 1.4826 * np.median(np.abs(np.diff(c, axis=1))) / np.sqrt(2)
        band = uniform_filter(c, 3) - uniform_filter(c, 31)
        out.append(float(np.sqrt(np.mean(band[16:-16, 16:-16] ** 2)) / max(sig, 1e-12)))
    return np.array(out)


blocked = 0; ta, tb = [], []
for line in open(os.path.join(A, "tiles-manifest.jsonl"), encoding="utf-8"):
    r = json.loads(line)
    if r["Frame"] != "master" or KEY not in r["SessionId"].lower() or "flip=" in r["SessionId"]:
        continue
    a, b = load(os.path.join(A, r["Tile"])), load(os.path.join(B, r["Tile"]))
    if (a == 0).all(axis=0).sum() >= 400 or (b == 0).all(axis=0).sum() >= 400:
        blocked += 1
        continue
    ta.append(texture(a)); tb.append(texture(b))
ta, tb = np.array(ta), np.array(tb)
f = lambda v: " / ".join(f"{x:.2f}" for x in v)
print(f"target tiles with a zero block in either export, left out: {blocked}; read: {len(ta)}")
print(f"texture, median: stored {f(np.median(ta, axis=0))}; grain {f(np.median(tb, axis=0))}")
print(f"texture, 90th percentile: stored {f(np.percentile(ta, 90, axis=0))}; grain {f(np.percentile(tb, 90, axis=0))}")
ratio = tb / np.maximum(ta, 1e-9)
print(f"grain over stored, per tile: median {f(np.median(ratio, axis=0))}; lowest {f(ratio.min(axis=0))}; highest {f(ratio.max(axis=0))}")
