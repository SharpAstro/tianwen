"""Make the remover's whole-frame stretch of one master softer or harder, changing nothing else (#2292, 2026-11-20).

    python -I stretch_soften.py <master> <out fits> <factor>

The remover stretches each channel from its minimum and its median above that minimum. This writes the master with
every channel's median above minimum multiplied by <factor>: over 1 one pixel a channel is planted lower (a softer
stretch), under 1 the pixels below the new minimum are raised to it (a harder one). Prints how many samples it touched."""
import sys
import numpy as np
from astropy.io import fits

SRC, OUT, FACTOR = sys.argv[1], sys.argv[2], float(sys.argv[3])
with fits.open(SRC) as h:
    hdr = h[0].header.copy()
    d = np.asarray(h[0].data, np.float32).copy()
shape = d.shape
d = d[None] if d.ndim == 2 else d          # a one-plane (mono) master has no channel axis
H, W = d.shape[-2:]
py, px = H // 2 + 7, 40
touched, before, after = 0, [], []
for c in range(d.shape[0]):
    ok = np.isfinite(d[c]) & (d[c] != 0)
    mn, med = float(d[c][ok].min()), float(np.median(d[c][ok]))
    want = med - FACTOR * (med - mn)
    before.append(med - mn)
    if want < mn:
        d[c, py + 2 * c, px] = want; touched += 1
    else:
        low = ok & (d[c] < want)
        touched += int(low.sum()); d[c][low] = want
    ok = np.isfinite(d[c]) & (d[c] != 0)
    after.append(float(np.median(d[c][ok])) - float(d[c][ok].min()))
print("median above min R/G/B: " + " / ".join(f"{v:.5g}" for v in before) + "  ->  " + " / ".join(f"{v:.5g}" for v in after) + f"   ({touched} samples touched of {d.size})")
fits.PrimaryHDU(d.reshape(shape), hdr).writeto(OUT, overwrite=True)
print(OUT)
