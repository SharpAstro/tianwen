"""Give one master another master's whole-frame stretch, changing nothing else (#2292, 2026-11-20).

    python -I stretch_swap.py <master to change> <master whose stretch to take> <out fits> [both | max | min | min:R | min:G | min:B]

The last argument takes only part of the other frame's stretch: its maximum alone, its minimums alone, or one channel's.

The remover stretches a frame per channel from three numbers: the frame's maximum (its unit divisor), the channel's
minimum, and the channel's median above that minimum. This writes <master to change> with those three made equal to the
other master's, by touching as few pixels as it can: one pixel planted at the new maximum, and per channel either one
pixel planted lower (to deepen the minimum) or the few pixels under the new minimum raised to it. Going to a deeper
minimum or a higher maximum touches 3 to 6 samples and nothing a star, a nebula or the noise is made of changes. The
reverse (a harder stretch, a lower maximum) is not like that: pixels under the new minimum are raised and every pixel
over the new maximum is clipped to it, which alters saturated star cores, so it touches hundreds. Prints the before and after of both frames."""
import sys
import numpy as np
from astropy.io import fits

SRC, LIKE, OUT = sys.argv[1:4]
WHAT = sys.argv[4] if len(sys.argv) > 4 else "both"
MINS = [c for c in range(3) if WHAT in ("both", "min", "min:" + "RGB"[c])]


def numbers(d):
    ok = np.isfinite(d) & (d != 0)
    return float(np.nanmax(d)), [(float(d[c][ok[c]].min()), float(np.median(d[c][ok[c]]))) for c in range(3)]


with fits.open(SRC) as h:
    hdr = h[0].header.copy()
    d = np.asarray(h[0].data, np.float32).copy()
like = np.asarray(fits.getdata(LIKE), np.float32)
top_s, ch_s = numbers(d)
top_l, ch_l = numbers(like)
del like
print(f"to change: max {top_s:.0f}, median above min R/G/B " + " / ".join(f"{m - n:.1f}" for n, m in ch_s))
print(f"to match : max {top_l:.0f}, median above min R/G/B " + " / ".join(f"{m - n:.1f}" for n, m in ch_l))
H, W = d.shape[-2:]
py, px = H // 2 + 7, 40            # a pixel of plain sky near the left edge, for the planted values
touched = 0
for c in MINS:
    mn, med = ch_s[c]
    want = med - (ch_l[c][1] - ch_l[c][0])          # the minimum that gives the other frame's median above minimum
    if want < mn:
        d[c, py + 2 * c, px] = want; touched += 1
    else:
        low = np.isfinite(d[c]) & (d[c] != 0) & (d[c] < want)
        touched += int(low.sum())
        d[c][low] = want
if WHAT not in ("both", "max"):
    pass
elif top_l > top_s:
    d[:, py + 8, px] = top_l; touched += 3
else:
    over = d > top_l
    touched += int(over.sum())
    d[over] = top_l
top_o, ch_o = numbers(d)
print(f"written  : max {top_o:.0f}, median above min R/G/B " + " / ".join(f"{m - n:.1f}" for n, m in ch_o) + f"   ({touched} samples touched of {d.size})")
fits.PrimaryHDU(d, hdr).writeto(OUT, overwrite=True)
print(OUT)
