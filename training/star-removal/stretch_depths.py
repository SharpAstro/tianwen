"""How deep under its sky each master's darkest pixel sits, per channel, in that channel's noise sigma (#2292, 2026-11-20).
    python -I stretch_depths.py <store> <out tsv>
That depth (median less minimum, over sigma) is what sets how hard the remover's whole-frame stretch pulls a channel."""
import os, sys
import numpy as np
from astropy.io import fits
STORE, OUT = sys.argv[1], sys.argv[2]
names = sorted(f for f in os.listdir(f"{STORE}/session-masters") if f.endswith(".fits") and "flip=" not in f)
rows = []
with open(OUT, "w", encoding="utf-8") as out:
    out.write("master\tchannels\tdepth_R\tdepth_G\tdepth_B\n")
    for k, name in enumerate(names):
        try:
            d = np.asarray(fits.getdata(f"{STORE}/session-masters/{name}"), np.float32)
        except Exception as e:
            print("SKIPPED", name, type(e).__name__); continue
        d = d[None] if d.ndim == 2 else d[:3]
        H, W = d.shape[-2:]
        dep = []
        for c in range(d.shape[0]):
            ok = np.isfinite(d[c]) & (d[c] != 0)
            if not ok.any(): dep.append(float("nan")); continue
            sig = 1.4826 * float(np.nanmedian(np.abs(np.diff(d[c, H // 2 - 256:H // 2 + 256, W // 2 - 256:W // 2 + 256], axis=1)))) / np.sqrt(2)
            dep.append((float(np.median(d[c][ok])) - float(d[c][ok].min())) / sig if sig > 0 else float("nan"))
        rows.append((name, dep))
        out.write(name[:-5] + f"\t{d.shape[0]}\t" + "\t".join(f"{v:.1f}" for v in (dep + [float('nan')] * 3)[:3]) + "\n"); out.flush()
        if (k + 1) % 30 == 0: print(k + 1, "of", len(names), flush=True)
col = [r for r in rows if len(r[1]) == 3 and all(np.isfinite(r[1]))]
allv = np.array([v for _, dep in col for v in dep])
print(f"{len(rows)} masters, {len(col)} with three channels")
print("depth of the darkest pixel under the sky, in sigma, all channels: " + " / ".join(f"{np.percentile(allv, p):.0f}" for p in (0, 10, 50, 90, 100)) + "  (min / 10th / median / 90th / max)")
ratio = np.array([max(dep) / min(dep) for _, dep in col])
print("largest over smallest channel depth within a master: " + " / ".join(f"{np.percentile(ratio, p):.2f}" for p in (0, 10, 50, 90, 100)) + f";  {int((ratio >= 2).sum())} of {len(col)} masters at 2 or more")
