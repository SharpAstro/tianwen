"""The glow left AROUND bright stars after removal, read on a whole master (#1871, plan: docs/plans/star-remover-training.md,
"Round 2: the hole under bright stars, read on whole masters"). The other rows of `score_whole.py` read a star's core and its
first few pixels; what an eye sees on a whole master is a wide, faint halo the remover left, which no per-pixel number
catches and a sum over the annulus does.

For each bright CLIPPED star of the INPUT master (a connected patch at or above --clip of the master's peak, no other bright
patch within 60 px, a compact core, the frame's covered pixels all round it), per channel:
  - the input and the starless plate are both put in the export's stretch with the INPUT's parameters, as
    `ChunkedNafnetRunner.ApplyInputStretch` does: unit = master / peak, per channel the minimum and the median of the
    shifted channel over COVERED pixels (finite, and not a zero or NaN canvas ring reachable from the border; an interior
    zero counts, as `Image.AbsentPixels` has it), then PixInsight's MTF at the balance that takes that median to 0.25;
  - the local sky is the mean of the stretched plate over the 45-60 px ring;
  - the excess is the stretched plate less that sky, summed over the 10-30 px annulus and divided by the master's own noise
    sigma in the same stretch (the MAD of horizontal neighbour differences over covered pixels), so a wide halo far below one
    sigma a pixel still counts as an eye sees it. The unit is sigma-pixels.
Printed per channel, in a fixed wording a scorer parses:
    glow <R|G|B> n <stars> median <x> p90 <x> above <share> % (threshold <t>)
The share is of stars whose summed excess is above the threshold, which is FIXED ONCE per master from the input's own stars:
`--derive-threshold` reads the same quantity on the INPUT itself (the halo before removal) and prints
    glow threshold <tR> <tG> <tB>
as --derive-fraction (default 0.1) of the input's per-channel median, i.e. a star counts as glowing when its plate keeps a
tenth of a typical halo. Write those three numbers into the config (`glow_threshold`, scorecard.example.json).

    python glow_ring.py <input master.fits> <starless.fits> --threshold tR,tG,tB
    python glow_ring.py <input master.fits> --derive-threshold [--derive-fraction 0.1]
A mono master (or plate) is read as three copies of its plane. Needs numpy, scipy and astropy; no GPU.
"""
import argparse
import sys

import numpy as np

INNER, OUTER = 10.0, 30.0           # the annulus the excess is summed over, px
SKY_IN, SKY_OUT = 45.0, 60.0        # the local sky ring, px
ISOLATION = 60.0                    # no other bright patch inside this radius
MAX_CORE = 15.0                     # a bright patch wider than this from its centre is not a compact star
TARGET_MEDIAN = 0.25
MIN_COVERED = 0.9                   # share of the ring and annulus that must be covered pixels

_Y, _X = np.mgrid[-60:61, -60:61]
RR = np.hypot(_X, _Y)
ANNULUS = (RR >= INNER) & (RR < OUTER)
SKY_RING = (RR >= SKY_IN) & (RR < SKY_OUT)
NEIGHBOURHOOD = RR < ISOLATION


def three_planes(data):
    data = np.asarray(data, dtype=np.float64)
    return np.repeat(data[None], 3, 0) if data.ndim == 2 else data


def covered_mask(plane):
    """Pixels the stretch measures: finite and not a zero/NaN canvas ring reachable from the border
    (an interior zero is a pixel; C# `Image.AbsentPixels`)."""
    from scipy.ndimage import label
    absent = ~np.isfinite(plane) | (plane == 0)
    lab, _ = label(absent)
    edge = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
    return ~np.isin(lab, edge[edge > 0])


def mtf(beta, x):
    xc = np.clip(x, 0.0, 1.0)
    return (beta - 1.0) * xc / ((2.0 * beta - 1.0) * xc - beta)


def midtones_balance_for(median, target=TARGET_MEDIAN):
    return median * (target - 1.0) / (target * (2.0 * median - 1.0) - median)


def stretch_params(master):
    """(divisor, mins[C], betas[C], covered[C]) from the master: the C# rule, on covered pixels."""
    divisor = float(max(np.nanmax(np.where(np.isfinite(master), master, -np.inf)), np.finfo(np.float64).tiny))
    mins, betas, covered = [], [], []
    for plane in master:
        cov = covered_mask(plane)
        unit = plane / divisor
        mn = float(unit[cov].min()) if cov.any() else 0.0
        med = float(np.median(unit[cov] - mn)) if cov.any() else 0.0
        mins.append(mn)
        betas.append(midtones_balance_for(med) if med > 0 else 0.5)
        covered.append(cov)
    return divisor, np.array(mins), np.array(betas), covered


def stretch(img, divisor, mins, betas):
    out = np.empty_like(img)
    for c in range(img.shape[0]):
        out[c] = mtf(betas[c], np.clip(img[c] / divisor - mins[c], 0.0, None))
    return out


def noise_sigma(stretched, covered):
    """Per channel, the MAD of horizontal neighbour differences over covered pixels, / sqrt 2."""
    sig = []
    for plane, cov in zip(stretched, covered):
        d = (plane[:, 1:] - plane[:, :-1])[cov[:, 1:] & cov[:, :-1]]
        sig.append(1.4826 * np.median(np.abs(d - np.median(d))) / np.sqrt(2))
    return np.array(sig)


def select_stars(master, divisor, covered, clip=0.95, bright=0.5):
    """(y, x) of each bright clipped, compact, isolated star with covered surroundings."""
    from scipy.ndimage import label, find_objects
    H, W = master.shape[-2:]
    unit = np.max(master, axis=0) / divisor
    lab, n = label(unit >= bright)
    allcov = np.all(covered, axis=0)
    found = []
    for i, sl in enumerate(find_objects(lab), start=1):
        comp = lab[sl] == i
        core = (unit[sl] >= clip) & comp
        if not core.any():
            continue
        ys, xs = np.nonzero(core)
        cy, cx = int(round(ys.mean())) + sl[0].start, int(round(xs.mean())) + sl[1].start
        if cy < 60 or cx < 60 or cy >= H - 60 or cx >= W - 60:
            continue
        cys, cxs = np.nonzero(comp)
        if np.hypot(cys + sl[0].start - cy, cxs + sl[1].start - cx).max() > MAX_CORE:
            continue
        win = lab[cy - 60:cy + 61, cx - 60:cx + 61]
        if np.any((win != 0) & (win != i) & NEIGHBOURHOOD):
            continue
        cw = allcov[cy - 60:cy + 61, cx - 60:cx + 61]
        if cw[ANNULUS].mean() < MIN_COVERED or cw[SKY_RING].mean() < MIN_COVERED:
            continue
        found.append((cy, cx))
    return found


def annulus_excess(stretched, sigma, stars):
    """(stars, C) summed excess of the stretched image over its own 45-60 px ring, in sigma-pixels."""
    out = np.empty((len(stars), stretched.shape[0]))
    for k, (y, x) in enumerate(stars):
        for c in range(stretched.shape[0]):
            win = stretched[c, y - 60:y + 61, x - 60:x + 61]
            ring = win[SKY_RING]
            ring = ring[np.isfinite(ring)]
            ann = win[ANNULUS]
            out[k, c] = np.nansum(ann - ring.mean()) / sigma[c]
    return out


def measure(master, plate):
    """(stars, excess of the plate (S, C), excess of the input itself (S, C)) for two (C, H, W) float arrays."""
    divisor, mins, betas, covered = stretch_params(master)
    s_in = stretch(master, divisor, mins, betas)
    s_pl = stretch(plate, divisor, mins, betas)
    sigma = noise_sigma(s_in, covered)
    stars = select_stars(master, divisor, covered)
    return stars, annulus_excess(s_pl, sigma, stars), annulus_excess(s_in, sigma, stars)


def main():
    from astropy.io import fits
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('master')
    ap.add_argument('plate', nargs='?')
    ap.add_argument('--threshold', help='tR,tG,tB: the per-channel threshold of the share, from --derive-threshold')
    ap.add_argument('--derive-threshold', action='store_true', help="read the INPUT's own halo and print the thresholds")
    ap.add_argument('--derive-fraction', type=float, default=0.1, help='the threshold as this share of the input median')
    a = ap.parse_args()
    master = three_planes(fits.getdata(a.master))
    if a.derive_threshold:
        plate = master
    elif a.plate and a.threshold:
        plate = three_planes(fits.getdata(a.plate))
    else:
        ap.error('give a plate and --threshold, or --derive-threshold')
    stars, ex, own = measure(master, plate)
    if not stars:
        sys.exit('no bright clipped isolated stars found in the master')
    if a.derive_threshold:
        thr = a.derive_fraction * np.median(own, axis=0)
        print(f'{len(stars)} stars; input halo median ' + ' '.join(f'{v:.2f}' for v in np.median(own, axis=0)))
        print('glow threshold ' + ' '.join(f'{v:.4f}' for v in thr))
        return
    thr = np.array([float(v) for v in a.threshold.split(',')])
    if thr.size != 3:
        ap.error('--threshold needs three numbers')
    for c, n in enumerate('RGB'):
        col = ex[:, c]
        print(f'glow {n} n {len(col)} median {np.median(col):.3f} p90 {np.percentile(col, 90):.3f} '
              f'above {np.mean(col > thr[c]) * 100:.1f} % (threshold {thr[c]:.4f})')


if __name__ == '__main__':
    main()
