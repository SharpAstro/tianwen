"""Is the grain rule's plate what the judge writes, cell by cell? (2026-11-21, a diagnostic.)

    python -I rule_against_judge_cells.py <stored plate fits> <the rule's plate fits> <our starless fits>
                                          <the judge's starless fits> <cell i,j> [<cell i,j>...]

All four are of one session's master and are read on the channels' mean. For each 300 px cell (band.py's grid, i down
and j across, with its 48 px margin), in the STORED plate's pixel sigma of the cell:

  - band.py's statistic (through a 7 px median, the 9 px box mean less the 41 px one, rms in sigma of a 9 px mean) of
    the stored plate, the rule's plate (plate_grain.py's output), ours and the judge's;
  - the layer the rule takes out of the plate (stored less rule): its mean and the rms of its 9 px mean;
  - what ours keeps over the judge's (ours less the judge's): the same two;
  - the correlation of the two layers' 9 px means over the cell.

A rule that did what the judge does would read the judge's band, and its layer would follow what ours keeps over the
judge's. The figures are larger than band.py's per-channel ones, since the channels' mean has less noise than a channel.
"""
import sys

import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

B, PAD = 300, 48


def lum(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


def band(a, sig):
    m = median_filter(a, 7)
    b = (uniform_filter(m, 9) - uniform_filter(m, 41))[PAD:-PAD, PAD:-PAD]
    return float(np.sqrt(np.mean(b ** 2)) / (sig / 9))


def mean9(a):
    return uniform_filter(a, 9)[PAD:-PAD, PAD:-PAD]


def main():
    stored, rule, ours, judge = (lum(p) for p in sys.argv[1:5])
    for spec in sys.argv[5:]:
        i, j = (int(v) for v in spec.split(","))
        cut = (slice(i * B - PAD, i * B + B + PAD), slice(j * B - PAD, j * B + B + PAD))
        st, ru, ou, ju = stored[cut], rule[cut], ours[cut], judge[cut]
        sig = 1.4826 * float(np.median(np.abs(np.diff(st[PAD:-PAD, PAD:-PAD], axis=1)))) / np.sqrt(2)
        taken, kept = st - ru, ou - ju
        both = np.corrcoef(mean9(taken).ravel(), mean9(kept - np.median(kept)).ravel())[0, 1]
        print(f"cell {i},{j}: band stored {band(st, sig):.1f}, rule {band(ru, sig):.1f}, ours {band(ou, sig):.1f}, "
              f"the judge {band(ju, sig):.1f} | stored less rule: mean {taken[PAD:-PAD, PAD:-PAD].mean() / sig:+.2f} sigma, "
              f"rms of its 9 px mean {mean9(taken).std() / sig:.2f} | ours less the judge: mean "
              f"{kept[PAD:-PAD, PAD:-PAD].mean() / sig:+.2f}, rms of its 9 px mean {mean9(kept).std() / sig:.2f} | "
              f"correlation of the two at 9 px {both:.2f}")


main()
