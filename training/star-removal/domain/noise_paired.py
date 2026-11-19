"""#1786: real against drawn at the very same cells, difference noise at a lag of 8 px over the recorded sigma.

    python -I noise_paired.py <store> <synthetic export> [<synthetic export> ...]

Per session of the first export, per channel, the median over the export's cells of: the real master's tile, the real
plate's, and for each export its draw and its clean tile (taken back to linear with the store's recorded stretch).
"""
import glob, json, os, re, sys
import numpy as np
from astropy.io import fits

S, exports = sys.argv[1], sys.argv[2:]
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"StretchOrigMin":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)
mtf = lambda b, x: (b - 1) * x / ((2 * b - 1) * x - b)


def sig(d):
    return 1.4826 * np.median(np.abs(d - np.median(d))) / np.sqrt(2)


for sess in sorted(os.listdir(f"{exports[0]}/tiles")):
    r = rows[sess]
    m = np.asarray(fits.getdata(f"{S}/session-masters/{sess}.fits"), np.float32)
    d = max(1.0, float(fits.getheader(f"{S}/session-masters/{sess}.fits").get("DATAMAX", 1.0)))
    p = np.asarray(fits.getdata(f"{S}/starless/plates/{sess}_plate.fits"), np.float32)
    acc = {}
    for f in sorted(glob.glob(f"{glob.escape(exports[0])}/tiles/{glob.escape(sess)}/*_deg000.f16")):
        x, y = map(int, re.search(r"x(-?\d+)_y(-?\d+)_", os.path.basename(f)).groups())
        if x < 0 or y < 0 or x + 256 > m.shape[2] or y + 256 > m.shape[1]:
            continue
        tiles = {"real master": m[:, y:y + 256, x:x + 256] / d, "real plate": p[:, y:y + 256, x:x + 256] / d}
        if not all(np.isfinite(t).all() for t in tiles.values()):
            continue
        for X in exports:
            g = f"{X}/tiles/{sess}/{os.path.basename(f)}"
            if not os.path.exists(g):
                continue
            for kind, path in (("draw", g), ("clean", g.replace("_deg000", "_master"))):
                a = np.fromfile(path, np.float16).astype(np.float64).reshape(3, 256, 256)
                tiles[f"{os.path.basename(X)} {kind}"] = np.stack([r["StretchOrigMin"][c] + mtf(1 - r["StretchBalance"][c], np.clip(a[c], 0, 1)) * (1 - r["StretchOrigMin"][c]) for c in range(3)])
        for name, t in tiles.items():
            for c in range(3):
                acc.setdefault((name, c), []).append(sig(t[c][:, 8:] - t[c][:, :-8]) / r["NoiseSigma"][c])
    print(sess[40:115])
    for name in sorted({k[0] for k in acc}, key=lambda n: (not n.startswith("real"), n)):
        print(f"   {name:28s} " + "  ".join(f"{np.median(acc[(name, c)]):.3f}" for c in range(3)) + f"   ({len(acc[(name, 0)])} cells)", flush=True)
