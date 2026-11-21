"""What the grain-rule plates change in an export's TARGET tiles (R4f's check before registration, 2026-11-20).
    python -I tile_grain.py <export from the stored plates> <export from the grain plates>
Per session both exports hold, over its cells' master tiles (the starless target a model trains toward, 256 px,
channels first, float16): how many are the same bytes, and for the rest the tile's texture between a 3 px and a 31 px
box mean, as rms over the tile's own pixel noise (neighbour differences), stored against grain, per channel. Also the
tile's mean level, to see that the rule moves no light."""
import json, os, sys
import numpy as np
from scipy.ndimage import uniform_filter

A, B = sys.argv[1], sys.argv[2]
T = 256


def load(path):
    raw = np.fromfile(path, "<f2").astype(np.float32)
    return raw.reshape(-1, T, T)


def texture(t):
    out = []
    for c in t:
        sig = 1.4826 * np.median(np.abs(np.diff(c, axis=1))) / np.sqrt(2)
        band = uniform_filter(c, 3) - uniform_filter(c, 31)
        out.append(float(np.sqrt(np.mean(band[16:-16, 16:-16] ** 2)) / max(sig, 1e-12)))
    return np.array(out)


by = {}
for line in open(os.path.join(A, "tiles-manifest.jsonl"), encoding="utf-8"):
    r = json.loads(line)
    if r["Frame"] == "master":
        by.setdefault(r["SessionId"], []).append(r["Tile"])
for session, tiles in by.items():
    same = 0; ta, tb, la, lb = [], [], [], []
    for rel in tiles:
        pa, pb = os.path.join(A, rel), os.path.join(B, rel)
        if not os.path.exists(pb):
            continue
        a, b = load(pa), load(pb)
        if a.shape == b.shape and np.array_equal(a, b):
            same += 1
            continue
        ta.append(texture(a)); tb.append(texture(b)); la.append(a.mean(axis=(1, 2))); lb.append(b.mean(axis=(1, 2)))
    print(session[:110])
    print(f"   target tiles {len(tiles)}; the same bytes in both exports: {same}; different: {len(ta)}")
    if ta:
        ta, tb, la, lb = map(np.array, (ta, tb, la, lb))
        f = lambda v: " / ".join(f"{x:.2f}" for x in v)
        print(f"   texture between 3 and 31 px, in pixel noise, median over the changed tiles, per channel: stored {f(np.median(ta, axis=0))}; grain {f(np.median(tb, axis=0))}")
        print(f"   the same at the 90th percentile: stored {f(np.percentile(ta, 90, axis=0))}; grain {f(np.percentile(tb, 90, axis=0))}")
        ratio = tb / np.maximum(ta, 1e-9)
        print(f"   grain over stored, per tile: median {f(np.median(ratio, axis=0))}; lowest {f(ratio.min(axis=0))}; highest {f(ratio.max(axis=0))}")
        print(f"   mean level, grain over stored: median {f(np.median(lb / np.maximum(la, 1e-12), axis=0))}")
