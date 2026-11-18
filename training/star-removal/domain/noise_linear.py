"""#1786: real and synthetic tiles taken back to linear with the store's recorded stretch (2026-11-18).

    python -I noise_linear.py <real store> <synthetic export> <session substring> ...

Per session and channel, the median over up to 60 tiles of: the level over the recorded background, and the difference
sigma at lags 1 and 8 over the master's recorded sigma. Three populations: the real master tiles, the synthetic clean
tiles (background and noise, no stars) and the synthetic draws (stars injected). Also the stars injected per tile.
"""
import glob, json, sys
import numpy as np

S, X = sys.argv[1], sys.argv[2]
rows, inj = {}, {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"StretchOrigMin":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)
for line in open(f"{X}/degradations.jsonl", encoding="utf-8"):
    r = json.loads(line); inj.setdefault(r["Tile"].split("/")[1], []).append(r.get("InjectedStars") or 0)
mtf = lambda b, x: (b - 1) * x / ((2 * b - 1) * x - b)


def sig(d):
    return 1.4826 * np.median(np.abs(d - np.median(d))) / np.sqrt(2)


for sess, r in sorted(rows.items()):
    if not any(w.lower() in sess.lower() for w in sys.argv[3:]) or not glob.glob(f"{X}/tiles/{glob.escape(sess)}/*_deg000.f16"):
        continue
    print(f"{sess[:90]}   stars injected per tile, median {np.median(inj.get(sess, [0])):.0f}")
    for label, pat in (("real", f"{S}/tiles/{glob.escape(sess)}/*_master.f16"), ("syn clean", f"{X}/tiles/{glob.escape(sess)}/*_master.f16"),
                       ("syn draw", f"{X}/tiles/{glob.escape(sess)}/*_deg000.f16")):
        out = []
        for f in sorted(glob.glob(pat))[:60]:
            a = np.fromfile(f, np.float16).astype(np.float64).reshape(-1, 256, 256); t = []
            for c in range(a.shape[0]):
                mn, b = r["StretchOrigMin"][c], r["StretchBalance"][c]
                lin = mn + mtf(1 - b, np.clip(a[c], 0, 1)) * (1 - mn)
                t.append((np.median(lin), sig(lin[:, 1:] - lin[:, :-1]), sig(lin[:, 8:] - lin[:, :-8])))
            out.append(t)
        if not out:
            continue
        o = np.median(np.array(out), axis=0)
        print(f"   {label:9s} " + "  ".join(f"ch{c} level {o[c, 0] / r['NoiseBackground'][c]:.3f} lag1 {o[c, 1] / r['NoiseSigma'][c]:.3f} lag8 {o[c, 2] / r['NoiseSigma'][c]:.3f}" for c in range(o.shape[0])), flush=True)
