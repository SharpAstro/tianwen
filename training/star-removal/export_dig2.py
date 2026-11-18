"""export_dig.py with the rim left in or out by environment variable (2026-11-16): RIM=16 (the default) leaves the 16 px rim
out as export_dig.py does, RIM=0 keeps it. Every third degraded tile of an export is read ([::3]); the twin figures quoted
for the rim in the R3w section come from twin_err_inner.py (quarter sample, i % 4), not from this script.

For every degraded tile of an export (x*_y*_degNNN.f16 with its .sigma.f16 plane) against its clean tile
(x*_y*_master.f16): the model's output less the clean tile in the tile's noise sigma, over the pixels where the model
took more than 1.5 sigma (twin_err.py's mask), inside the RIM px rim.

    [RIM=0] python -I export_dig2.py <weights folder> <weights.pt> <export root> [<export root> ...]   (from the trainer copy's folder)
"""
import glob, os, sys
import numpy as np
from scipy.ndimage import gaussian_filter, binary_dilation, median_filter

sys.path.insert(0, ".")
import n2n_smoke as S
import torch

folder, name, roots = sys.argv[1], sys.argv[2], sys.argv[3:]
rd = lambda p: np.fromfile(p, "<f2").reshape(3, 256, 256).astype(np.float32)
rp = lambda p: np.fromfile(p, "<f2").reshape(256, 256).astype(np.float32)
noise = lambda img: np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])
dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
B = int(os.environ.get("RIM","16"))
print(f"{name}")
print(f"{'export':24s} tiles  rms in mask  rms outside  smooth(2px) rms in mask  mean in mask  masked share   (sigma)")
for root in roots:
    files = sorted(f for f in glob.glob(os.path.join(root, "tiles", "*", "*_deg*.f16")) if not f.endswith(".sigma.f16"))[::3]
    x = np.stack([rd(f) for f in files]); y = np.stack([rd(f[:f.rindex("_deg")] + "_master.f16") for f in files])
    pl = np.stack([rp(f[:-4] + ".sigma.f16") for f in files])
    out = S.denoise(folder, name, x, dev, planes=pl)
    acc = [[], [], [], [], []]
    for i in range(len(x)):
        sig = noise(x[i])[:, None, None]
        m = binary_dilation(np.stack([gaussian_filter(c, 1.0) for c in (x[i] - out[i]) / sig]).max(0) > 1.5, iterations=2)
        
        if B: m[:B] = m[-B:] = False; m[:, :B] = m[:, -B:] = False
        inner = np.zeros_like(m); inner[B:256-B, B:256-B] = True
        if not m.any():
            continue
        e = (out[i] - y[i]) / sig
        lo = np.stack([gaussian_filter(c, 2.0) for c in e])
        acc[0].append((e[:, m] ** 2).mean()); acc[1].append((e[:, inner & ~m] ** 2).mean())
        acc[2].append((lo[:, m] ** 2).mean()); acc[3].append(e[:, m].mean()); acc[4].append(m.sum() / inner.sum())
    print(f"{os.path.basename(root)[:24]:24s} {len(x):5d} {np.sqrt(np.mean(acc[0])):10.3f} {np.sqrt(np.mean(acc[1])):12.3f} "
          f"{np.sqrt(np.mean(acc[2])):18.3f} {np.mean(acc[3]):18.3f} {100 * np.mean(acc[4]):12.1f}%", flush=True)
