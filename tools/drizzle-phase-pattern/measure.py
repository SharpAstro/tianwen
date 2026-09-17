"""Read-only: does a master carry the 2x2 Bayer-phase level pattern? (#292 for normalised drizzle,
IntegrationOptions.DrizzleSkyReference for the dataset bake's unnormalised one; the measurements are in
docs/architecture/stacking-render-pipeline.md.)

    python tools/drizzle-phase-pattern/measure.py <folder of master FITS> [drizzle]

`drizzle` limits it to STRATEGY = 'BayerDrizzle' masters. Per channel, on the central 1536 px square,
background pixels only:

  col = (median(x[:, 2k] - x[:, 2k+1]) - median(x[:, 2k+1] - x[:, 2k+2])) / 2
  row = the same down the rows

A period-2 alternation of amplitude a gives +a and -a for the two halves, so their half-difference
is a; a smooth gradient gives the same sign twice and cancels. Divided by the per-pixel noise
(1.4826 MAD of the adjacent differences / sqrt 2), so it reads in the same sigma units as #292's
table (R 1.05 / 0.59, B 0.80 / 1.02 unfixed; about 0.02 fixed). Also reports what fraction of the
even-minus-odd differences share the sign of the median, 50 percent being pure noise."""
import os
import sys
import numpy as np
from astropy.io import fits

ARGS = [a for a in sys.argv[1:] if a != 'drizzle']
if not ARGS:
    sys.exit('usage: measure.py <folder or FITS file>... [drizzle]')
PATHS = []
for a in ARGS:
    PATHS += sorted(os.path.join(a, n) for n in os.listdir(a) if n.lower().endswith('.fits')) if os.path.isdir(a) else [a]
HALF = 768


def alternation(p):
    """(amplitude, sigma, sign agreement) of an along-x period-2 alternation over the rows of p."""
    a = p[:, 0:-2:2] - p[:, 1:-1:2]
    b = p[:, 1:-1:2] - p[:, 2::2]
    a = a[np.isfinite(a)]
    b = b[np.isfinite(b)]
    both = np.concatenate([a, -b])
    sigma = 1.4826 * np.median(np.abs(both - np.median(both))) / np.sqrt(2)
    amp = (np.median(a) - np.median(b)) / 2
    agree = np.mean(np.sign(both) == np.sign(np.median(both))) if amp != 0 else 0.5
    return amp, sigma, agree


def measure(plane):
    """[column, row, checkerboard], each (amplitude in sigma, sign agreement). A checkerboard flips sign
    from row to row, so it cancels out of the column and row terms (green's pattern read 0.00 there
    while plainly visible at 10x): it is the half-difference of the column term over even and odd rows."""
    finite = np.isfinite(plane)
    lo, hi = np.nanpercentile(plane[finite], [5, 55])
    p = np.where(finite & (plane >= lo) & (plane <= hi), plane, np.nan)
    out = []
    for q in (p, p.T):
        amp, sigma, agree = alternation(q)
        out.append((amp / sigma if sigma > 0 else float('nan'), agree))
    even_amp, sigma, even_agree = alternation(p[0::2])
    odd_amp, _, odd_agree = alternation(p[1::2])
    out.append(((even_amp - odd_amp) / 2 / sigma if sigma > 0 else float('nan'), (even_agree + odd_agree) / 2))
    return out


rows = []
for path in PATHS:
    name = os.path.basename(path)
    with fits.open(path, memmap=True) as hdul:
        h = hdul[0].header
        strategy = h.get('STRATEGY')
        if 'drizzle' in sys.argv[1:] and strategy != 'BayerDrizzle':
            continue
        data = hdul[0].data
        if data.ndim == 2:
            data = data[np.newaxis]
        c, H, W = data.shape
        y0, x0 = H // 2 - HALF, W // 2 - HALF
        crop = np.array(data[:, y0:y0 + 2 * HALF, x0:x0 + 2 * HALF], dtype=np.float64)
    res = [measure(crop[ch]) for ch in range(c)]
    line = f"{strategy[:12]:12s} " + "  ".join(
        f"{'RGB'[ch] if c == 3 else 'M'} col {r[0][0]:6.3f} ({r[0][1]*100:3.0f}%) row {r[1][0]:6.3f} ({r[1][1]*100:3.0f}%) chk {r[2][0]:6.3f} ({r[2][1]*100:3.0f}%)" for ch, r in enumerate(res)) + f"  {name[:60]}"
    print(line, flush=True)
