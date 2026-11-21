"""Where does the grain a starless plate holds sit: on the stars the catalogue has, or between them? (2026-11-22.)

    python -I grain_tied_to_stars.py <plate stars.csv> [--median <px>] <label>=<plate fits> [...] --cells <i,j> [<i,j>...]

The first plate named sets the unit (its pixel sigma in the cell, from neighbours' differences); name the judge's
first. For each 300 px cell (band.py's grid, 48 px margin) and each plate, on the channels' mean, the grain map is the
plate's 9 px box mean less its 41 px one: what the grain statistic reads, as a map. Without --median a star's own
residue is counted; --median 7 puts the plate through band.py's 7 px median first, which is what the registered rows
read. Printed per plate:

  - the map's rms in that unit (white noise alone would read 1/9; a master's noise is correlated and reads more);
  - the share of the map's power (the sum of its squares about the cell's mean) that lies within 4 px of a
    catalogued star of 10 sigma and over, and within 4 px of any catalogued star, each beside the share of the
    cell's AREA those pixels are. Grain that has nothing to do with the catalogued stars has a power share equal to
    the area share; what a subtraction leaves on a star lifts it;
  - how much of the map a linear fit on the catalogued stars explains: the catalogue binned by significance (5 to
    7, 7 to 10, 10 to 20, 20 to 50, 50 and over), each bin's count map spread by a Gaussian of 2, 4 and 8 px and put
    through the same box means, fitted by least squares with an intercept and no bound on a coefficient's sign (so
    a dark pit on a star fits as well as light on it) on one half of the cell and read on the other, both ways (the
    mean of the two out-of-sample shares; under zero means worse than no fit at all). It sees only what is the
    same for every star of a bin: a residue of either sign from star to star fits nothing, and the power shares
    are what says where such a residue sits.

The master itself, stars and all, is the control: its power sits on the stars and, without --median, the fit
explains a good part of it. Through --median 7 the master's power still sits 77, 68 and 78 % on the bright stars,
and the fit is a weak control (7, 20 and 40 % on the master by cell): the power shares are the control.
"""
import csv
import sys

import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, maximum_filter, median_filter, uniform_filter

B, PAD = 300, 48
BINS = ((5, 7), (7, 10), (10, 20), (20, 50), (50, 1e9))
WIDTHS = (2.0, 4.0, 8.0)
NEAR_PX = 4


def lum(path):
    a = np.asarray(fits.getdata(path), np.float32)
    return a[:3].mean(axis=0) if a.ndim == 3 else a


def passed(a):
    return (uniform_filter(a, 9) - uniform_filter(a, 41))[PAD:-PAD, PAD:-PAD]


def explained(y, maps):
    half = y.shape[1] // 2
    shares = []
    for fit_cols, read_cols in ((slice(0, half), slice(half, None)), (slice(half, None), slice(0, half))):
        a = np.stack([m[:, fit_cols].ravel() for m in maps] + [np.ones(y[:, fit_cols].size)], axis=1)
        coef, *_ = np.linalg.lstsq(a, y[:, fit_cols].ravel(), rcond=None)
        b = np.stack([m[:, read_cols].ravel() for m in maps] + [np.ones(y[:, read_cols].size)], axis=1)
        t = y[:, read_cols].ravel()
        shares.append(1 - np.sum((t - b @ coef) ** 2) / np.sum((t - t.mean()) ** 2))
    return float(np.mean(shares))


def main():
    median_px = 0
    if "--median" in sys.argv:
        at = sys.argv.index("--median")
        median_px = int(sys.argv[at + 1])
        del sys.argv[at:at + 2]
    cut_at = sys.argv.index("--cells")
    rows = list(csv.DictReader(open(sys.argv[1], encoding="utf-8")))
    x = np.array([float(r["x"]) for r in rows])
    y = np.array([float(r["y"]) for r in rows])
    sig = np.array([float(r["significance"]) for r in rows])
    plates = [(s.split("=", 1)[0], lum(s.split("=", 1)[1])) for s in sys.argv[2:cut_at]]
    side = B + 2 * PAD
    yy, xx = np.mgrid[-NEAR_PX:NEAR_PX + 1, -NEAR_PX:NEAR_PX + 1]
    disc = (yy ** 2 + xx ** 2) <= NEAR_PX ** 2
    for spec in sys.argv[cut_at + 1:]:
        i, j = (int(v) for v in spec.split(","))
        top, left = i * B - PAD, j * B - PAD
        cut = (slice(top, top + side), slice(left, left + side))
        near = (y >= top) & (y < top + side) & (x >= left) & (x < left + side)
        maps, total, bright = [], np.zeros((side, side)), np.zeros((side, side))
        for lo, hi in BINS:
            pick = near & (sig >= lo) & (sig < hi)
            count = np.zeros((side, side))
            np.add.at(count, ((y[pick] - top).astype(int), (x[pick] - left).astype(int)), 1)
            total += count
            if lo >= 10:
                bright += count
            maps += [passed(gaussian_filter(count, w)) for w in WIDTHS]
        by_bright = (maximum_filter(bright, footprint=disc) > 0)[PAD:-PAD, PAD:-PAD]
        by_any = (maximum_filter(total, footprint=disc) > 0)[PAD:-PAD, PAD:-PAD]
        unit_cell = plates[0][1][cut][PAD:-PAD, PAD:-PAD]
        pixel = 1.4826 * float(np.median(np.abs(np.diff(unit_cell, axis=1)))) / np.sqrt(2)
        inner = near & (y >= i * B) & (y < i * B + B) & (x >= j * B) & (x < j * B + B)
        print(f"cell {i},{j}: {int(inner.sum())} catalogued stars, {int((inner & (sig >= 10)).sum())} of 10 sigma and over; within "
              f"{NEAR_PX} px of one of those {100 * by_bright.mean():.0f} % of the cell, of any catalogued star {100 * by_any.mean():.0f} %")
        for label, plate in plates:
            grain = passed(median_filter(plate[cut], median_px) if median_px else plate[cut])
            power = (grain - grain.mean()) ** 2
            print(f"  {label:26s} rms {grain.std() / pixel:6.2f}; power by a star of 10 sigma and over {100 * power[by_bright].sum() / power.sum():5.1f} %, "
                  f"by any catalogued star {100 * power[by_any].sum() / power.sum():5.1f} %; a linear fit on the catalogued stars explains {100 * explained(grain, maps):6.1f} %")


main()
