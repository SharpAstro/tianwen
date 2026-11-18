"""#1786: how much of the sky the stars cover, real against drawn (2026-11-18).

    python -I noise_starlayer.py <real store> <synthetic export> <session substring> ...

Real: the retained master less its starless plate, both divided by the master's DATAMAX (the unit scale). Drawn: each
draw's tile less its clean tile, both taken back to linear with the store's recorded stretch (the draw adds the stars and
their shot noise to the very planes the clean tile shows). Per channel: the share of pixels where the star layer is over
0.5, 1, 2, 5 and 20 of the master's recorded sigma, and the layer's mean in sigma.
"""
import glob, json, sys
import numpy as np
from astropy.io import fits

S, X = sys.argv[1], sys.argv[2]
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"StretchOrigMin":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)
mtf = lambda b, x: (b - 1) * x / ((2 * b - 1) * x - b)
K = (0.5, 1, 2, 5, 20)


def line(label, layer, s):
    v = layer[np.isfinite(layer)] / s
    return f"   {label:6s} " + " ".join(f">{k:g}: {100 * (v > k).mean():5.2f}%" for k in K) + f"  mean {v.mean():.3f}  median {np.median(v):+.3f}"


for sess, r in sorted(rows.items()):
    if not any(w.lower() in sess.lower() for w in sys.argv[3:]) or not glob.glob(f"{X}/tiles/{glob.escape(sess)}/*_deg000.f16"):
        continue
    print(sess[:95])
    m = np.asarray(fits.getdata(f"{S}/session-masters/{sess}.fits"), np.float32)
    d = max(1.0, float(fits.getheader(f"{S}/session-masters/{sess}.fits").get("DATAMAX", 1.0)))
    p = np.asarray(fits.getdata(f"{S}/starless/plates/{sess}_plate.fits"), np.float32)
    m = m[None] if m.ndim == 2 else m; p = p[None] if p.ndim == 2 else p
    H, W = m.shape[1:]
    real = (m - p)[:, H // 8:7 * H // 8, W // 8:7 * W // 8] / d
    C = m.shape[0]
    syn = [[] for _ in range(C)]
    for f in sorted(glob.glob(f"{X}/tiles/{glob.escape(sess)}/*_deg000.f16")):
        a = np.fromfile(f, np.float16).astype(np.float64).reshape(-1, 256, 256)
        b = np.fromfile(f.replace("_deg000.f16", "_master.f16"), np.float16).astype(np.float64).reshape(-1, 256, 256)
        for c in range(C):
            mn, bal = r["StretchOrigMin"][c], r["StretchBalance"][c]
            syn[c].append((mtf(1 - bal, np.clip(a[c], 0, 1)) - mtf(1 - bal, np.clip(b[c], 0, 1))) * (1 - mn))
    for c in range(C):
        s = r["NoiseSigma"][c]
        print(f"  ch{c}"); print(line("real", real[c], s)); print(line("drawn", np.array(syn[c]), s), flush=True)
