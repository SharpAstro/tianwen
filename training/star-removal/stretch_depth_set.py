"""Set the remover's black point of one master a fixed number of noise sigma under each channel's sky (#2309, 2026-11-20).

    python -I stretch_depth_set.py <master> <out fits> <k sigma> [centre]

The remover stretches each channel from its minimum. This writes the master with every channel's minimum at its median
less <k sigma> of that channel's own sky noise, changing nothing else: a pixel planted where the minimum has to go down,
the pixels under it raised where it has to come up. It is the rule "median less a fixed number of noise sigma" tried at
inference on a model that was not trained with it. A mono master (one plane) is handled as one channel.

The sky noise of a channel is the 20th percentile, over the frame's 64 px blocks that hold no canvas ring, of each
block's neighbour-difference sigma (1.4826 x the median absolute difference of horizontal neighbours, over root 2): the
quiet part of the frame, wherever it is. The first version read a central 512 px crop instead, which is 30 Doradus on
the Tarantula master and reads 1.2 to 1.6 times this figure on the masters tried; "centre" as a last argument asks for
that reading again, to reproduce the frames written with it (those planted at a fixed spot near the left edge, which
"centre" does too). Otherwise a planted pixel goes on plain sky (a 9 px box within 3 sigma of the median, nearest the
middle of the frame's left half), never on a star or in the canvas ring."""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter

SRC, OUT, K = sys.argv[1], sys.argv[2], float(sys.argv[3])
CENTRE = len(sys.argv) > 4 and sys.argv[4] == "centre"
BLOCK = 64


def sky_sigma(p):
    H, W = p.shape
    if CENTRE:
        return 1.4826 * float(np.median(np.abs(np.diff(p[H // 2 - 256:H // 2 + 256, W // 2 - 256:W // 2 + 256], axis=1)))) / np.sqrt(2)
    hh, ww = H // BLOCK * BLOCK, (W - 1) // BLOCK * BLOCK
    bad = ~np.isfinite(p) | (p == 0)
    # A difference counts only when both of its pixels are there: a block whose last column borders a NaN ring would
    # otherwise hand a NaN to its median, and one NaN block made the whole percentile NaN (the Ophiuchus master's ring).
    d = np.where(bad[:, :-1] | bad[:, 1:], np.nan, np.abs(np.diff(p, axis=1)))[:hh, :ww]
    blocks = d.reshape(hh // BLOCK, BLOCK, ww // BLOCK, BLOCK).transpose(0, 2, 1, 3).reshape(hh // BLOCK, ww // BLOCK, -1)
    whole = np.isfinite(blocks).all(axis=2)
    if not whole.any():
        sys.exit(f"FAILED: no {BLOCK} px block of {SRC} is free of the canvas ring, so there is no sky noise to set a depth by")
    return float(np.percentile(1.4826 * np.median(blocks[whole], axis=1) / np.sqrt(2), 20))


with fits.open(SRC) as h:
    hdr = h[0].header.copy()
    d = np.asarray(h[0].data, np.float32).copy()
planes = d[None] if d.ndim == 2 else d
C, H, W = planes.shape
touched, before, spots = 0, [], []
for c in range(C):
    p = planes[c]
    ok = np.isfinite(p) & (p != 0)
    mn, med = float(p[ok].min()), float(np.median(p[ok]))
    sig = sky_sigma(p)
    if not (np.isfinite(sig) and sig > 0):
        sys.exit(f"FAILED: channel {c} of {SRC} gives a sky noise of {sig}; nothing was written")
    before.append((med - mn) / sig)
    want = med - K * sig
    if want < mn:
        if CENTRE:
            y, x = H // 2 + 7 + 2 * c, 40
        else:
            plain = uniform_filter((ok & (np.abs(p - med) < 3 * sig)).astype(np.float32), 9) > 0.999
            ys, xs = np.nonzero(plain)
            if ys.size == 0:
                sys.exit(f"FAILED: channel {c} of {SRC} has no plain 9 px box of sky to plant its black point in; nothing was written")
            i = int(np.argmin((ys - H // 2) ** 2 + (xs - W // 4 - 2 * c) ** 2))
            y, x = int(ys[i]), int(xs[i])
        p[y, x] = want; touched += 1; spots.append((x, y))
    else:
        low = ok & (p < want)
        touched += int(low.sum()); p[low] = want
# One reading is named, the one that was taken: "sky noise" is the quiet blocks' figure, and the central crop's is not it.
print(("depth in the central crop's noise sigma" if CENTRE else "depth in sky noise sigma") + ": " + " / ".join(f"{v:.1f}" for v in before)
      + f"  ->  {K:g} each   ({touched} samples touched of {d.size}" + (f"; planted at {spots}" if spots else "") + ")")
fits.PrimaryHDU(d, hdr).writeto(OUT, overwrite=True)
print(OUT)
