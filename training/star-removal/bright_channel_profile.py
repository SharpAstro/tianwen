"""Bright-star channel profiles (2026-11-11, plan: "M42's bright stars: the channels' wings"). Usage: python -I bright_channel_profile.py <model> ...
Radial median of (output - input's 19-22 px plane)/sigma, per channel and luminance, for real-val stars of 100-1000 sigma,
unsaturated, per session. Also the INPUT less the plane (the star + nebula) and SXT, so a dig can be told from nebula taken."""
import csv, json, os, sys
from collections import defaultdict
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter
# Where the inputs live: TIANWEN_SR_SCRATCH (the scratch root) and TIANWEN_SR_PLATES (the bake's starless plates),
# each defaulting to DE1's folder. Read from the environment because these scripts run with python -I.
SCRATCH = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch")
PLATES = os.environ.get("TIANWEN_SR_PLATES", "D:/Astro-Dataset/2026-10-12-full/starless/plates")
REALX = f"{SCRATCH}/r2-real-val"; EVAL = f"{SCRATCH}/f2-eval-master"; SXT = f"{SCRATCH}/sxt-real-val"
SIZE, CH, W = 256, 3, 22
yy, xx = np.mgrid[-W:W + 1, -W:W + 1]; rr = np.hypot(xx, yy); FAR = (rr >= 19) & (rr <= 22)
RADII = [0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 18]
models = sys.argv[1:]
def read(p): return np.fromfile(p, "<f2").reshape(CH, SIZE, SIZE).astype(np.float32)
def noise(img): return np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])
def plane(win, ring):
    X = np.stack([np.ones(ring.sum()), xx[ring], yy[ring]], 1); out = np.empty_like(win)
    for c in range(CH):
        k = np.linalg.lstsq(X, win[c][ring], rcond=None)[0]; out[c] = k[0] + k[1] * xx + k[2] * yy
    return out
def prof(d, sig):
    z = d / sig[:, None, None]
    return np.array([[np.median(z[c][(rr >= r - 0.5) & (rr < r + 0.5)]) for r in RADII] for c in range(CH)])
rows = [json.loads(l) for l in open(f"{REALX}/injections.jsonl", encoding="utf-8") if l.strip()]
def outs(n):
    if n == "sxt": return {r["Tile"]: ("fits", f"{SXT}/in/{i:06d}_starless.fits") for i, r in enumerate(rows)}
    d = f"{EVAL}/{n}/on-real-val"
    return {r["Tile"]: ("f16", f"{d}/{r['Output']}") for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
names = ["sxt"] + models; O = {n: outs(n) for n in names}; cats = {}
P = defaultdict(lambda: defaultdict(list)); FARLEVEL = defaultdict(list)
for r in rows:
    sid = r["SessionId"]; sess = sid.split("|")[2][:16]
    if sid not in cats:
        cats[sid] = [(float(s["x"]), float(s["y"]), float(s["significance"])) for s in csv.DictReader(open(f"{PLATES}/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv", encoding="utf-8"))
                     if s["saturated"] != "1" and s["significance"] not in ("", "NaN") and 100 <= float(s["significance"]) < 1000]
    if not cats[sid]: continue
    inp = read(f"{REALX}/{r['Tile']}"); sig = noise(inp); imgs = {}
    for n in names:
        kind, p = O[n][r["Tile"]]; imgs[n] = read(p) if kind == "f16" else np.asarray(fits.getdata(p), dtype=np.float32)
    for x, y, z in cats[sid]:
        xi, yi = int(round(x - r["CellX"])), int(round(y - r["CellY"]))
        if not (16 + W <= xi < SIZE - 16 - W and 16 + W <= yi < SIZE - 16 - W): continue
        sl = (slice(None), slice(yi - W, yi + W + 1), slice(xi - W, xi + W + 1))
        ref = plane(inp[sl], FAR)
        # How much structure the far ring holds: the ring's own rms about its plane, in sigma (nebula texture).
        FARLEVEL[sess].append(float(np.mean([np.std((inp[sl][c] - ref[c])[FAR]) / sig[c] for c in range(CH)])))
        P[sess]["input"].append(prof(inp[sl] - ref, sig))
        for n in names:
            w = imgs[n][sl]
            if np.isnan(w).any(): continue
            P[sess][n].append(prof(w - ref, sig))
for s, d in P.items():
    print()
    print(s + " per channel: input normalised to its r=0, then each model in sigma")
    for n, v in d.items():
        a = np.median(np.array(v), 0)
        for c, cn in enumerate("RGB"):
            vals = a[c] / a[c][0] if n == "input" else a[c]
            print(f"  {n[:16]:16s} {cn} " + "".join(f"{x:7.2f}" for x in vals[:9]))
