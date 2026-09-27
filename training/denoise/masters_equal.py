"""Compare master FITS files pairwise, bit for bit (NaN equal to NaN): `python masters_equal.py old new [old new ...]`.

Prints one line per pair, `SAME` or `DIFF`, the largest absolute difference and the new file's name. Used to prove
a restack changed only what it was meant to (run-poolrf-evalrf.ps1: the ring fix is in the tile export, not the
master).
"""
import os
import sys

import numpy as np
from astropy.io import fits

for a, b in zip(sys.argv[1::2], sys.argv[2::2]):
    x, y = fits.getdata(a).astype(np.float64), fits.getdata(b).astype(np.float64)
    if x.shape != y.shape:
        print(f'DIFF maxdiff n/a {os.path.basename(b)}')
        continue
    same = np.array_equal(np.isnan(x), np.isnan(y)) and np.array_equal(np.nan_to_num(x), np.nan_to_num(y))
    print(f"{'SAME' if same else 'DIFF'} maxdiff {np.nanmax(np.abs(x - y)):.3g} {os.path.basename(b)}")
