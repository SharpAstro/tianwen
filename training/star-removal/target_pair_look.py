"""R4f's targets beside R4d's at 1:1 (2026-11-20): the same cells of one session from two exports.
    python -I target_pair_look.py <export a> <export b> <session folder substring> <out png> [cells, default 6]
Cells both exports hold, ranked by how much the target's 9 to 41 px band changed between them (the largest falls first).
Per cell two rows of three tiles, export a's target / export b's target / export b's input draw 0: as the model sees them
(the master's stretch, 0 to 1), then each about its own median with six times the contrast (texture). Prints per cell the
band of each target (rms of the 9 px box mean less the 41 px box mean, luminance, in units of 1e-3)."""
import os, sys
import numpy as np
from PIL import Image
from scipy.ndimage import uniform_filter

A, B, KEY, OUT = sys.argv[1:5]
N = int(sys.argv[5]) if len(sys.argv) > 5 else 6
T = 256


def load(path):
    a = np.fromfile(path, "<f2").astype(np.float32).reshape(-1, T, T)
    return np.repeat(a, 3, axis=0) if a.shape[0] == 1 else a[:3]


def band(a):
    l = a.mean(axis=0)
    return 1000 * float(np.std(uniform_filter(l, 9) - uniform_filter(l, 41)))


sess = [s for s in os.listdir(os.path.join(A, "tiles")) if KEY.lower() in s.lower() and "flip=" not in s]
assert len(sess) == 1, sess
da, db = os.path.join(A, "tiles", sess[0]), os.path.join(B, "tiles", sess[0])
cells = sorted({f[:-len("_master.f16")] for f in os.listdir(da) if f.endswith("_master.f16")} & {f[:-len("_master.f16")] for f in os.listdir(db) if f.endswith("_master.f16")})
rows = []
for c in cells:
    ta, tb = load(f"{da}/{c}_master.f16"), load(f"{db}/{c}_master.f16")
    rows.append((band(tb) - band(ta), band(ta), band(tb), c, ta, tb))
rows.sort(key=lambda r: r[0])
print(f"{sess[0][:80]}: {len(cells)} cells in both; band of the target, a then b, the largest falls first")
out = []
for d, ba, bb, c, ta, tb in rows[:N]:
    inp = load(f"{db}/{c}_deg000.f16")
    print(f"  {c}: {ba:.2f} -> {bb:.2f}; level {1000 * float(np.median(ta)):.0f} -> {1000 * float(np.median(tb)):.0f}")
    as_is = np.concatenate([np.clip(t, 0, 1).transpose(1, 2, 0) for t in (ta, tb, inp)], axis=1)
    tex = np.concatenate([np.clip((t - np.median(t, axis=(1, 2), keepdims=True)) * 6 + 0.5, 0, 1).transpose(1, 2, 0) for t in (ta, tb, inp)], axis=1)
    out += [as_is, tex]
Image.fromarray((np.concatenate(out, axis=0) * 255 + 0.5).astype(np.uint8)).save(OUT)
print(f"{OUT}: columns a's target / b's target / b's input; per cell as seen, then about the median at six times the contrast")
print(f"unchanged cells (band within 2 %): {sum(abs(r[0]) <= 0.02 * max(r[1], 1e-9) for r in rows)} of {len(rows)}")
