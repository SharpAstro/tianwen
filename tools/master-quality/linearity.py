"""Is a master linear, or has something already stretched it?

The two look alike in a single statistic and separate cleanly on three:
  - dynamic range, p99.9 / median. A linear astro frame's stars sit orders of magnitude over sky.
  - the pile-up at the ceiling. Clipping makes a spike at max; a stretch makes none.
  - the shape of the faint end. Linear sky noise is near symmetric about the median in ADU;
    an MTF-stretched sky is squashed on one side.
"""
import sys
import numpy as np
from astropy.io import fits

KEYS = ("BITPIX", "EXPTIME", "EXPOSURE", "GAIN", "OFFSET", "SATURATE", "STACK_N", "INSTRUME",
        "FILTER", "SWCREATE", "BZERO", "BSCALE", "PEDESTAL", "MAXADU")


def look(path, label):
    with fits.open(path, memmap=False) as hdul:
        hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
        hdr = hdu.header
        cube = np.asarray(hdu.data, dtype=np.float32)
    if cube.ndim == 2:
        cube = cube[None, ...]
    p = cube[0]
    v = p[np.isfinite(p)]
    mx = float(v.max())
    q = np.percentile(v, [0.1, 1, 50, 99, 99.9, 99.99])
    # pile-up: pixels within 0.1% of the observed maximum
    pile = float(np.mean(v >= mx * 0.999))
    med = q[2]
    # faint-end symmetry about the median, in raw units
    lo_half = med - np.percentile(v, 16)
    hi_half = np.percentile(v, 84) - med

    print(f"=== {label}")
    print("    " + "  ".join(f"{k}={hdr[k]}" for k in KEYS if k in hdr))
    print(f"    max {mx:.6g}   median {med:.6g}   median/max {med/mx:.4f}")
    print(f"    p0.1 {q[0]:.5g}  p1 {q[1]:.5g}  p99 {q[3]:.5g}  p99.9 {q[4]:.5g}  p99.99 {q[5]:.5g}")
    print(f"    dynamic range p99.9/median = {q[4]/med:6.2f}      p99.99/median = {q[5]/med:6.2f}")
    print(f"    pixels within 0.1% of max  = {pile*100:.4f}%  ({int(pile*v.size):,} px)")
    print(f"    sky spread about median    = -{lo_half:.5g} / +{hi_half:.5g}  "
          f"(ratio {hi_half/max(lo_half,1e-12):.2f})")


for i in range(1, len(sys.argv), 2):
    look(sys.argv[i], sys.argv[i + 1])
