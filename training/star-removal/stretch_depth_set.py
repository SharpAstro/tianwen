"""Set the remover's black point of one master a fixed number of noise sigma under each channel's sky (#2309, 2026-11-20).

    python -I stretch_depth_set.py <master> <out fits> <k sigma>

The remover stretches each channel from its minimum. This writes the master with every channel's minimum at its median
less <k sigma> of that channel's own noise (neighbour differences of a central 512 px crop), changing nothing else: a
pixel planted where the minimum has to go down, the pixels under it raised where it has to come up. It is the rule
"median less a fixed number of noise sigma" tried at inference on a model that was not trained with it."""
import sys
import numpy as np
from astropy.io import fits

SRC, OUT, K = sys.argv[1], sys.argv[2], float(sys.argv[3])
with fits.open(SRC) as h:
    hdr = h[0].header.copy()
    d = np.asarray(h[0].data, np.float32).copy()
H, W = d.shape[-2:]
py, px = H // 2 + 7, 40
touched, before = 0, []
for c in range(3):
    ok = np.isfinite(d[c]) & (d[c] != 0)
    mn, med = float(d[c][ok].min()), float(np.median(d[c][ok]))
    sig = 1.4826 * float(np.median(np.abs(np.diff(d[c, H // 2 - 256:H // 2 + 256, W // 2 - 256:W // 2 + 256], axis=1)))) / np.sqrt(2)
    before.append((med - mn) / sig)
    want = med - K * sig
    if want < mn:
        d[c, py + 2 * c, px] = want; touched += 1
    else:
        low = ok & (d[c] < want)
        touched += int(low.sum()); d[c][low] = want
print("depth in noise sigma R/G/B: " + " / ".join(f"{v:.1f}" for v in before) + f"  ->  {K:g} each   ({touched} samples touched of {d.size})")
fits.PrimaryHDU(d, hdr).writeto(OUT, overwrite=True)
print(OUT)
