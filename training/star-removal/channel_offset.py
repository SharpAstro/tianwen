"""Channel centroid offsets and widths (2026-11-11, plan: "M42's bright stars: the channels' wings"). Usage: python -I channel_offset.py
Per channel, the centroid offset from green (px) and the second-moment width ratio to green, of real-val stars at
100-1000 sigma unsaturated, from the input tile less its 8-11 px ring median; median and spread per session."""
import csv, json, os
from collections import defaultdict
import numpy as np
# Where the inputs live: TIANWEN_SR_SCRATCH (the scratch root) and TIANWEN_SR_PLATES (the bake's starless plates),
# each defaulting to DE1's folder. Read from the environment because these scripts run with python -I.
SCRATCH = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch")
PLATES = os.environ.get("TIANWEN_SR_PLATES", "D:/Astro-Dataset/2026-10-12-full/starless/plates")
REALX = f"{SCRATCH}/r2-real-val"
SIZE, CH, W = 256, 3, 11
yy, xx = np.mgrid[-W:W + 1, -W:W + 1]; rr = np.hypot(xx, yy); RING = (rr >= 8) & (rr <= 11); IN = rr <= 4
def read(p): return np.fromfile(p, "<f2").reshape(CH, SIZE, SIZE).astype(np.float32)
rows = [json.loads(l) for l in open(f"{REALX}/injections.jsonl", encoding="utf-8") if l.strip()]
cats = {}; R = defaultdict(list)
for r in rows:
    sid = r["SessionId"]; sess = sid.split("|")[2][:16]
    if sid not in cats:
        cats[sid] = [(float(s["x"]), float(s["y"])) for s in csv.DictReader(open(f"{PLATES}/{sid.replace('/', '_').replace('|', '_')}_plate.stars.csv", encoding="utf-8"))
                     if s["saturated"] != "1" and s["significance"] not in ("", "NaN") and 100 <= float(s["significance"]) < 1000]
    if not cats[sid]: continue
    inp = read(f"{REALX}/{r['Tile']}")
    for x, y in cats[sid]:
        xi, yi = int(round(x - r["CellX"])), int(round(y - r["CellY"]))
        if not (W + 2 <= xi < SIZE - W - 2 and W + 2 <= yi < SIZE - W - 2): continue
        win = inp[:, yi - W:yi + W + 1, xi - W:xi + W + 1]
        m = []
        for c in range(CH):
            w = np.clip(win[c] - np.median(win[c][RING]), 0, None) * IN
            s = w.sum()
            if s <= 0: break
            cx, cy = (w * xx).sum() / s, (w * yy).sum() / s
            m.append((cx, cy, np.sqrt((w * ((xx - cx) ** 2 + (yy - cy) ** 2)).sum() / s)))
        if len(m) == CH: R[sess].append(m)
for s, v in R.items():
    a = np.array(v)  # n, ch, (cx, cy, width)
    d = a[:, :, :2] - a[:, 1:2, :2]
    print(f"{s:16s} n={len(a):4d}  R-G dx/dy {np.median(d[:,0,0]):+.3f} {np.median(d[:,0,1]):+.3f} (|d| {np.median(np.hypot(d[:,0,0],d[:,0,1])):.3f})"
          f"   B-G dx/dy {np.median(d[:,2,0]):+.3f} {np.median(d[:,2,1]):+.3f} (|d| {np.median(np.hypot(d[:,2,0],d[:,2,1])):.3f})"
          f"   width R/G {np.median(a[:,0,2]/a[:,1,2]):.3f} B/G {np.median(a[:,2,2]/a[:,1,2]):.3f}")
