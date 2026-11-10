"""Where does the saturated-star dig sit? Radial median of (output - reference) in sigma, per model.
Real val: reference = plane fitted on the INPUT's 19-22 px ring (far from the star), so a removed halo reads 0 and a dig < 0.
Truth (on-random): reference = the truth plate, exact.
"""
import csv, json, sys
from collections import defaultdict
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter

REALX = "C:/temp/tianwen-scratch/r2-real-val"; TWINX = "C:/temp/tianwen-scratch/f2-random-master"
EVAL = "C:/temp/tianwen-scratch/f2-eval-master"; SXT = "C:/temp/tianwen-scratch/sxt-real-val"
PLATES = "D:/Astro-Dataset/2026-10-12-full/starless/plates"
SIZE, CH, W = 256, 3, 22
yy, xx = np.mgrid[-W:W + 1, -W:W + 1]; rr = np.hypot(xx, yy)
FAR = (rr >= 19) & (rr <= 22)
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
    # luminance-ish: mean over channels of d/sig, radial medians
    g = (d / sig[:, None, None]).mean(0)
    return [np.median(g[(rr >= r - 0.5) & (rr < r + 0.5)]) for r in RADII]

def report(title, P):
    print(title)
    print("    " + "".join(f"{r:>7d}" for r in RADII))
    for n, v in P.items():
        a = np.array(v)
        print(f"  {n[:14]:14s} n={len(a):3d}  " + "".join(f"{x:7.2f}" for x in np.median(a, 0)))

# real val
rows = [json.loads(l) for l in open(f"{REALX}/injections.jsonl", encoding="utf-8") if l.strip()]
def outs(n):
    if n == "sxt": return {r["Tile"]: ("fits", f"{SXT}/in/{i:06d}_starless.fits") for i, r in enumerate(rows)}
    d = f"{EVAL}/{n}/on-real-val"
    return {r["Tile"]: ("f16", f"{d}/{r['Output']}") for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
names = ["sxt"] + models
O = {n: outs(n) for n in names}
cats = {}
P = defaultdict(list); Psess = defaultdict(lambda: defaultdict(list))
for r in rows:
    sid = r["SessionId"]
    if sid not in cats:
        cats[sid] = [(float(s["x"]), float(s["y"])) for s in csv.DictReader(open(f"{PLATES}/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv", encoding="utf-8"))
                     if s["saturated"] == "1" and s["outcome"] == "Subtracted"]
    inp = read(f"{REALX}/{r['Tile']}"); sig = noise(inp)
    imgs = {}
    for n in names:
        kind, p = O[n][r["Tile"]]
        imgs[n] = read(p) if kind == "f16" else np.asarray(fits.getdata(p), dtype=np.float32)
    for x, y in cats[sid]:
        xi, yi = int(round(x - r["CellX"])), int(round(y - r["CellY"]))
        if not (16 + W <= xi < SIZE - 16 - W and 16 + W <= yi < SIZE - 16 - W): continue
        sl = (slice(None), slice(yi - W, yi + W + 1), slice(xi - W, xi + W + 1))
        ref = plane(inp[sl], FAR)
        P["input"].append(prof(inp[sl] - ref, sig))
        for n in names:
            w = imgs[n][sl]
            if np.isnan(w).any(): break
            p = prof(w - ref, sig); P[n].append(p); Psess[sid.split("|")[2][:14]][n].append(p)
report("REAL VAL saturated, (output - input's far plane)/sigma, radial median", P)
for s, d in Psess.items():
    report(f" {s}", d)

# truth tiles
T = defaultdict(list)
trows = [json.loads(l) for l in open(f"{TWINX}/injections.jsonl", encoding="utf-8") if l.strip()]
tout = {}
for m in models:
    d = f"{EVAL}/{m}/on-random"
    tout[m] = {r["Tile"]: f"{d}/{r['Output']}" for r in map(json.loads, open(f"{d}/outputs.jsonl", encoding="utf-8"))}
for r in trows:
    if any(r["Tile"] not in tout[m] for m in models): continue
    sat = [s for s in r["Stars"] if s.get("Saturated") and not s.get("Lobe")]
    if not sat: continue
    p = f"{TWINX}/{r['Tile']}"; draw = read(p); plate = read(p[:p.rindex("_deg")] + "_master.f16"); sig = noise(draw)
    for s in sat:
        xi, yi = int(round(s["X"])), int(round(s["Y"]))
        if not (W <= xi < SIZE - W and W <= yi < SIZE - W): continue
        sl = (slice(None), slice(yi - W, yi + W + 1), slice(xi - W, xi + W + 1))
        T["input"].append(prof(draw[sl] - plate[sl], sig))
        for m in models:
            T[m].append(prof(read(tout[m][r["Tile"]])[sl] - plate[sl], sig))
report("TRUTH on-random saturated, (output - truth)/sigma, radial median", T)
