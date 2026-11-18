"""#1786: every session's starless plate noise over its master's recorded sigma (2026-11-18).

    python -I noise_plate_all.py <store> <out.tsv>

Per session: over 128 px blocks of the middle three quarters of the frame, the lag 8 difference sigma (1.4826 MAD over
sqrt 2) of the master and of the plate, each over the recorded NoiseSigma, the median over blocks and channels; and the
catalogue's subtracted stars per 256 px tile.
"""
import csv, json, os, sys
import numpy as np
from astropy.io import fits

S, OUT = sys.argv[1], sys.argv[2]
rows = {}
for line in open(f"{S}/tiles-manifest.jsonl", encoding="utf-8"):
    if '"Frame":"master"' in line and '"NoiseSigma":[' in line:
        r = json.loads(line); rows.setdefault(r["Tile"].split("/")[1], r)


def sig(d):
    return 1.4826 * np.median(np.abs(d - np.median(d))) / np.sqrt(2)


done = set(l.split("\t")[0] for l in open(OUT, encoding="utf-8")) if os.path.exists(OUT) else set()
out = open(OUT, "a", encoding="utf-8")
for sess, r in sorted(rows.items()):
    mp, pp = f"{S}/session-masters/{sess}.fits", f"{S}/starless/plates/{sess}_plate.fits"
    if sess in done or not (os.path.exists(mp) and os.path.exists(pp)):
        continue
    m = np.asarray(fits.getdata(mp), np.float32); p = np.asarray(fits.getdata(pp), np.float32)
    d = max(1.0, float(fits.getheader(mp).get("DATAMAX", 1.0)))
    m = m[None] if m.ndim == 2 else m; p = p[None] if p.ndim == 2 else p
    C, H, W = m.shape
    n = sum(1 for s in csv.DictReader(open(pp[:-5] + ".stars.csv", encoding="utf-8")) if s["outcome"] == "Subtracted")
    rm, rp = [], []
    for c in range(C):
        s = r["NoiseSigma"][c] * d; a, b = [], []
        for y in range(H // 8, 7 * H // 8 - 128, 128):
            for x in range(W // 8, 7 * W // 8 - 136, 128):
                t, u = m[c, y:y + 128, x:x + 136], p[c, y:y + 128, x:x + 136]
                if np.isfinite(t).all() and np.isfinite(u).all() and (t != 0).all():
                    a.append(sig(t[:, 8:] - t[:, :-8]) / s); b.append(sig(u[:, 8:] - u[:, :-8]) / s)
        if a:
            rm.append(np.median(a)); rp.append(np.median(b))
    if rm:
        out.write(f"{sess}\t{n * 65536 / (W * H):.0f}\t{np.mean(rm):.3f}\t{np.mean(rp):.3f}\t{min(rp):.3f}\t{max(rp):.3f}\n"); out.flush()
