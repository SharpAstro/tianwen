"""A two-sided read of the 9 to 41 px band against the judge's own plate, cell by cell (2026-11-21, a trial row).

    python -I judge_band_rows.py <the judge's starless plate fits> [--choose-by <plate fits>] <label>=<plate fits> ...

--choose-by sorts the cells by another plate's band (in the judge's sigma, by the same two levels) and leaves the rest
as it is: every figure is still read against the judge's plate. It answers whether a ratio over the judge's leans as
it does only because the judge's own band chose the cells.

R4f's added row (2) read "the grain in the dense cells" on two cells picked by eye, and one of them held structure the
judge keeps. Here the judge's plate sorts the cells: every 300 px cell of the frame away from its border (band.py's
grid and statistic, on the channels' mean, in the judge's own pixel sigma of the cell) is FLAT where the judge's band is
under 1.5 (noise alone reads 0.85), STRUCTURE where it is 3 or over, and neither between. Per plate: on the flat cells
its median band and the share of them it leaves under 1.5 (the mottle, which a remover should take); on the structure
cells the median of its band over the judge's and the share within 0.8 to 1.25, with the shares under and over that
window (the detail, which it should keep; a plate that leaves its mottle on those cells reads over). A rule that
smooths reads well on the first and low on the second; a model that leaves the mottle the other way round.
A cell that holds an absent pixel, or exact zeros on over 1 % of it, is left out.
"""
import sys

import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, uniform_filter

B, PAD = 300, 48
FLAT, STRUCTURE = 1.5, 3.0


def lum(path):
    a = np.asarray(fits.getdata(path), np.float32)
    if a.ndim == 2:
        return a
    with np.errstate(invalid="ignore"):
        return a[:3].mean(axis=0)


def usable(cut):
    return bool(np.isfinite(cut).all() and (cut == 0).mean() <= 0.01)


def bands(img, sig):
    out = {}
    for (i, j), s in sig.items():
        cut = img[i * B - PAD:i * B + B + PAD, j * B - PAD:j * B + B + PAD]
        if not usable(cut):
            continue
        p7 = median_filter(cut, 7)
        b = (uniform_filter(p7, 9) - uniform_filter(p7, 41))[PAD:-PAD, PAD:-PAD]
        out[(i, j)] = float(np.sqrt(np.mean(b ** 2)) / (s / 9))
    return out


def main():
    specs = sys.argv[2:]
    choose_by = None
    if "--choose-by" in specs:
        at = specs.index("--choose-by")
        choose_by = specs[at + 1]
        del specs[at:at + 2]
    judge = lum(sys.argv[1])
    h, w = judge.shape
    sig = {}
    for i in range(1, h // B - 1):
        for j in range(1, w // B - 1):
            if usable(judge[i * B - PAD:i * B + B + PAD, j * B - PAD:j * B + B + PAD]):
                sig[(i, j)] = 1.4826 * float(np.median(np.abs(np.diff(judge[i * B:i * B + B, j * B:j * B + B], axis=1)))) / np.sqrt(2)
    bj = bands(judge, sig)
    chosen = {c: v for c, v in bands(lum(choose_by), sig).items() if c in bj} if choose_by else bj
    flat = [c for c, v in chosen.items() if v < FLAT]
    structure = [c for c, v in chosen.items() if v >= STRUCTURE]
    who = "the plate named by --choose-by" if choose_by else "the judge's plate"
    print(f"{len(bj)} cells read; {who} is flat (band under {FLAT:g}) on {len(flat)} and holds structure ({STRUCTURE:g} or over) on {len(structure)}"
          + (f"; the judge's median band on the flat ones {np.median([bj[c] for c in flat]):.2f}" if flat else ""))
    print(f"  {'plate':40s} flat cells: median band, share under {FLAT:g} | structure cells: band over the judge's, median, share within 0.8 to 1.25")
    for spec in specs:
        label, path = spec.split("=", 1)
        b = bands(lum(path), sig)
        f = np.array([b[c] for c in flat if c in b])
        s = np.array([b[c] / bj[c] for c in structure if c in b])
        left = f"{np.median(f):6.2f}  {100 * (f < FLAT).mean():5.1f} %" if len(f) else "   no flat cell"
        right = (f"{np.median(s):5.2f}  {100 * ((s >= 0.8) & (s <= 1.25)).mean():5.1f} %  ({100 * (s < 0.8).mean():.1f} % under, "
                 f"{100 * (s > 1.25).mean():.1f} % over)") if len(s) else "no structure cell"
        print(f"  {label:40s} {left}                  |  {right}")


main()
