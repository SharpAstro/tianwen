"""Does the model dig on its OWN kind of data? (2026-11-16, after R3w)

twin_err.py reads the truth error under removed stars on the twin: stars drawn on REAL starless plates. This reads the
same figures on the training cache's held-out cells: stars drawn on the SYNTHETIC background, the distribution the loss
is computed on. If the bias is there too, the loss leaves it; if it is not, the dig is what a real plate's background
does to the model, and no loss term on the training data can take it out.

    python -I train_dig.py <cache> <weights.pt> [<weights.pt> ...]     (run from the frozen trainer copy's folder)
"""
import sys
import numpy as np
from scipy.ndimage import gaussian_filter, binary_dilation, median_filter

sys.path.insert(0, ".")
import n2n_smoke as S
import torch

cache, names = sys.argv[1], sys.argv[2:]
mm, meta = S.open_cache(cache)
sig_planes, has = S.open_sigma(cache, meta)
n_train = meta["train_cells"]
cells = list(range(n_train, meta["cells"]))[::2]           # every second held-out cell
DRAWS = [1, 2]                                              # two of the four draws
B = 16                                                      # the rim the loss masks
noise = lambda img: np.array([1.4826 * np.median(np.abs((h := c - median_filter(c, 5)) - np.median(h))) for c in img])
dev = torch.device("cuda" if torch.cuda.is_available() else "cpu")
x = np.stack([np.asarray(mm[c, d], dtype=np.float32) for c in cells for d in DRAWS])
y = np.stack([np.asarray(mm[c, S.SLOT_MASTER], dtype=np.float32) for c in cells for d in DRAWS])
pl = np.stack([np.asarray(sig_planes[c, d], dtype=np.float32) for c in cells for d in DRAWS])
print(f"{len(cells)} held-out cells x {len(DRAWS)} draws from {cache}")
print(f"{'model':28s} rms in mask  rms outside  smooth(2px) rms in mask  mean in mask   masked share  (sigma)")
for name in names:
    out = S.denoise(cache, name, x, dev, planes=pl)
    acc = [[], [], [], [], []]
    for i in range(len(x)):
        sig = noise(x[i])[:, None, None]
        m = binary_dilation(np.stack([gaussian_filter(c, 1.0) for c in (x[i] - out[i]) / sig]).max(0) > 1.5, iterations=2)
        m[:B] = m[-B:] = False; m[:, :B] = m[:, -B:] = False
        inner = np.zeros_like(m); inner[B:-B, B:-B] = True
        if not m.any():
            continue
        e = (out[i] - y[i]) / sig
        lo = np.stack([gaussian_filter(c, 2.0) for c in e])
        acc[0].append((e[:, m] ** 2).mean()); acc[1].append((e[:, inner & ~m] ** 2).mean())
        acc[2].append((lo[:, m] ** 2).mean()); acc[3].append(e[:, m].mean()); acc[4].append(m.sum() / inner.sum())
    print(f"{name[:28]:28s} {np.sqrt(np.mean(acc[0])):10.3f} {np.sqrt(np.mean(acc[1])):12.3f} {np.sqrt(np.mean(acc[2])):18.3f} "
          f"{np.mean(acc[3]):18.3f} {100 * np.mean(acc[4]):12.1f}%")
