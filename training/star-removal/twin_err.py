"""Twin truth error of a model's output against the plate, in the draw's noise sigma: over the pixels the post-step's mask
covers (taken from the base model) and outside them. python -I twin_err.py <base> <model...>"""
import json, os, sys
import numpy as np
from scipy.ndimage import gaussian_filter, binary_dilation, median_filter
S = os.environ.get("TIANWEN_SR_SCRATCH", "C:/temp/tianwen-scratch"); T = f"{S}/f2-random-master"; E = f"{S}/f2-eval-master"
rd = lambda p: np.fromfile(p, "<f2").reshape(3, 256, 256).astype(np.float32)
noise = lambda img: np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])
base, models = sys.argv[1], sys.argv[1:]
O = {m: {r["Tile"]: r["Output"] for r in map(json.loads, open(f"{E}/{m}/on-random/outputs.jsonl", encoding="utf-8"))} for m in models}
acc = {m: [[], [], [], []] for m in models}
for i, t in enumerate(O[base]):
    if i % 4: continue
    p = f"{T}/{t}"; draw = rd(p); plate = rd(p[:p.rindex("_deg")] + "_master.f16"); sig = noise(draw)[:, None, None]
    b = rd(f"{E}/{base}/on-random/{O[base][t]}")
    m = binary_dilation(np.stack([gaussian_filter(c, 1.0) for c in (draw - b) / sig]).max(0) > 1.5, iterations=2)
    for n in models:
        e = (rd(f"{E}/{n}/on-random/{O[n][t]}") - plate) / sig
        lo = np.stack([gaussian_filter(c, 2.0) for c in e])
        acc[n][0].append((e[:, m] ** 2).mean()); acc[n][1].append((e[:, ~m] ** 2).mean())
        acc[n][2].append((lo[:, m] ** 2).mean()); acc[n][3].append(e[:, m].mean())
print(f"{'model':28s} rms in mask  rms outside  smooth(2px) rms in mask  mean in mask   (sigma)")
for n in models:
    a = acc[n]; print(f"{n:28s} {np.sqrt(np.mean(a[0])):10.3f} {np.sqrt(np.mean(a[1])):12.3f} {np.sqrt(np.mean(a[2])):18.3f} {np.mean(a[3]):18.3f}")
