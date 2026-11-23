"""What a black point set by sky noise does on a frame whose noise is no longer its own (#2341, 2026-11-23).

    python -I smoothed_black_point.py <master> <out smoothed fits> [--depth 40] [--smooth 1.5]

R4h's and R4i's registration asks for it, reported for R4i and deciding nothing: a model whose contract states a depth
has its black point at `depth` sky-noise sigma under each channel's median. On a denoised frame that noise is small, so
the black point lies close under the sky and dark structure can fall beneath it. This writes the master smoothed by a
Gaussian of --smooth px (its canvas ring, the zeros or NaN that reach the border, kept as it is) and prints, per channel,
for the master and for the smoothed copy:

  - the sky noise as the product reads it (Image.TryEstimateSkyNoise mirrored: 64 px blocks, a block's noise 1.4826
    times the median absolute difference of horizontal neighbours over root two, the 20 % quantile over the blocks that
    touch no absent pixel and are not flat);
  - the black point, the channel's median over covered pixels less depth times that noise, and how far under the
    median that is in the MASTER's noise;
  - the share of covered pixels under it;
  - where those pixels sit on the smoothed copy: the share of them in connected patches of 50 px or more (a lane, a
    globule) against smaller ones (a hole, a dead pixel).
The remover is then run on the smoothed copy by hand (image remove-stars) and nebula_rows.py reads its plate against it.
"""
import argparse

import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, label

p = argparse.ArgumentParser()
p.add_argument("master")
p.add_argument("out")
p.add_argument("--depth", type=float, default=40.0)
p.add_argument("--smooth", type=float, default=1.5)
a = p.parse_args()
BLOCK, QUANTILE = 64, 0.20

hdr = fits.getheader(a.master)
raw = np.asarray(fits.getdata(a.master)).astype(np.float64)
planes = raw[None] if raw.ndim == 2 else raw
C, H, W = planes.shape

# The canvas ring: pixels that are not finite, or zero in every channel, and reach the border.
empty = ~np.isfinite(planes).all(axis=0) | (planes == 0).all(axis=0)
lab, _ = label(empty)
edge = np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))
absent = np.isin(lab, edge[edge > 0])
covered = ~absent


def sky_noise(plane):
    """Image.TryEstimateSkyNoise, mirrored."""
    bx, by = (W - 1) // BLOCK, H // BLOCK
    sig = []
    for j in range(by):
        for i in range(bx):
            ys, xs = slice(j * BLOCK, (j + 1) * BLOCK), slice(i * BLOCK, i * BLOCK + BLOCK + 1)
            if absent[ys, xs].any():
                continue
            d = np.abs(np.diff(plane[ys, xs], axis=1))
            if not np.isfinite(d).all():
                continue
            s = 1.4826 / np.sqrt(2) * float(np.median(d))
            if s > 0:
                sig.append(s)
    if not sig:
        # Image.TryEstimateSkyNoise answers false here, and its caller says so.
        raise SystemExit("no sky noise to read: every 64 px block touches the canvas ring or is flat")
    sig = np.sort(np.array(sig))
    rank = QUANTILE * (len(sig) - 1)
    lo = int(np.floor(rank))
    hi = min(lo + 1, len(sig) - 1)
    return float(sig[lo] + (rank - lo) * (sig[hi] - sig[lo])), len(sig)


filled = np.where(covered[None], np.nan_to_num(planes), 0.0)
weight = gaussian_filter(covered.astype(np.float64), a.smooth)
smoothed = np.stack([np.where(covered, gaussian_filter(filled[c], a.smooth) / np.maximum(weight, 1e-6), planes[c]) for c in range(C)])
out = smoothed[0] if raw.ndim == 2 else smoothed
fits.writeto(a.out, out.astype(np.float32), hdr, overwrite=True)

print(f"{a.master}: {C} channel(s), {W} x {H}, covered {100 * covered.mean():.1f} %; depth {a.depth:g} sigma, smoothed by {a.smooth:g} px")
for c in range(C):
    own, blocks = sky_noise(planes[c])
    for name, img in (("master", planes[c]), ("smoothed", smoothed[c])):
        sigma, _ = sky_noise(img)
        v = img[covered]
        med = float(np.median(v))
        black = med - a.depth * sigma
        under = (img < black) & covered
        lab_u, n = label(under)
        sizes = np.bincount(lab_u.ravel())[1:] if n else np.array([], int)
        big = int(sizes[sizes >= 50].sum()) if n else 0
        print(f"  channel {c} {name:8s}: sky noise {sigma:.4g} ({sigma / own:.2f} of the master's; {blocks} blocks), black point "
              f"{a.depth * sigma / own:.1f} master sigma under the median, covered pixels under it {100 * under.sum() / covered.sum():.3f} % "
              f"({int(under.sum())} px; in patches of 50 px or more {100 * big / max(1, under.sum()):.0f} %, {int((sizes >= 50).sum()) if n else 0} patches)")
