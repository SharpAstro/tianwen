"""One starless plate out of several removers' plates of one master: the teacher of the consolidation arm
(docs/plans/star-remover-training.md, "R5a and R5b"). No model is run here: plates in, one plate out.

    python -I teacher_combine.py <out fits> <master fits> --off med|med2|low
        --any <plate> [...] --pair <plate> <plate> --trusted <plate> [...] [--mask-out <fits>]

Every plate is a member's starless plate of the SAME master, on its grid and scale (`image remove-stars --model ...
--no-protect-cores --no-fill-digs --keep-overshoot`). The members are `--any`, `--pair` and `--trusted` together.

The MEDIAN PLATE is the per-pixel median of all the members' plates: the typical member, with its broad light, its
halos and its knots. A member's EXTRA is what it takes beyond the median plate (the median plate less its own, where
that is positive), and the extra's COMPACT part is what a 9 x 9 grey opening removes from it: light narrower than
9 px, a star's. The output is the median plate less a compact extra, chosen by where the pixel stands:

  ON bright nebula    the compact extra of ANY trusted member, or of BOTH members of the pair (the larger of: the
                      trusted members' largest compact extra, the pair's smaller one). On bright nebula a compact
                      peak may be a knot of the nebula, so one member's word is taken only from the members that
                      keep the knots.
  OFF bright nebula   `--off med`: the largest compact extra of ANY member, so every compact star any member found
                      goes and broad light stays the typical member's.
                      `--off med2`: the SECOND largest compact extra, so a compact star goes when at least two
                      members took it, and one member's mistake on a master nobody looked at is not taught.
                      `--off low`: no median plate there, the LOWEST of all the members' plates: a star or a lump
                      any member took is taken, and the level is the lowest member's.

Pooling is not free where the members disagree pixel by pixel. On the Tarantula's crowded field the ten members of
the plan's check stand 0.32 pixel sigma apart about their median plate, and the largest compact extra of the ten
(`--off med`) lowers the plate by 0.43 sigma a pixel beyond it; on dark sky they stand 0.04 to 0.10 apart and it
lowers the plate by 0.07 to 0.15 (measured 2026-11-28, green, the medians of 400 px boxes). So `--off med` also
takes some of a crowded field's level, less than `--off low` does.

Bright nebula is `nebula_rows.py`'s: the master's large-scale map (8 px block medians, median filtered over 7
blocks) 20 pixel sigmas or more over the map's 5th percentile. The two regimes are blended over a soft edge of about
16 px.

Why every pixel is built from ONE surface. A first construction took a compact part from some members and a broad
part from others and added them. Where the members disagree about a broad feature (a bright knot most of them take
and two keep) the two parts overlap, their sum takes more than the input holds over the plate, and the output is a
dark patch with coloured debris, seen at 1:1 on the Tarantula's knots on 2026-11-27. Here the compact extra is
measured against the same median plate it is taken from, so the output never goes under the lowest member's plate
nor over the median plate, which `test_teacher_combine.py` pins.
"""
import argparse

import numpy as np
from astropy.io import fits
from scipy.ndimage import gaussian_filter, grey_opening, median_filter

BLOCK, FILTER, NEBULA_SIGMA = 8, 7, 20.0
OPENING = 9
EDGE_SIGMA = 8.0


def nebula_weight(lum, valid=None):
    """0 off the bright nebula, 1 well inside it, a soft edge between (`lum` is the master's luminance, no NaN).
    `valid` marks the pixels the master has data in: the sky level (5th percentile of the blocks) and the pixel sigma
    are taken over those only, never over an absent ring, whose zeros would pull the sky to 0 and read the whole
    frame as bright nebula."""
    h, w = lum.shape
    if valid is None:
        valid = np.ones(lum.shape, bool)
    rows = lum[::4]
    both = valid[::4][:, 1:] & valid[::4][:, :-1]
    diffs = np.abs(np.diff(rows, axis=1))
    sig = 1.4826 * float(np.median(diffs[both] if both.any() else diffs)) / np.sqrt(2)
    bh, bw = h // BLOCK, w // BLOCK
    blocks = np.median(lum[:bh * BLOCK, :bw * BLOCK].reshape(bh, BLOCK, bw, BLOCK).transpose(0, 2, 1, 3).reshape(bh, bw, -1), axis=2)
    live = valid[:bh * BLOCK, :bw * BLOCK].reshape(bh, BLOCK, bw, BLOCK).all(axis=(1, 3))
    small = median_filter(blocks, FILTER)
    big = np.zeros_like(lum)
    big[:bh * BLOCK, :bw * BLOCK] = np.repeat(np.repeat(small, BLOCK, 0), BLOCK, 1)
    sky = float(np.percentile(small[live], 5)) if live.any() else float(np.percentile(small, 5))
    neb = (big - sky > NEBULA_SIGMA * sig).astype(np.float32)
    return np.clip(gaussian_filter(neb, EDGE_SIGMA), 0.0, 1.0), float(neb.mean())


def combine_plane(stack, pair, trusted, weight, off):
    """One channel: `stack` holds the members' plates (no NaN), `pair` and `trusted` their indices in it."""
    med = np.median(stack, axis=0)
    extra_trusted = extra_pair = None
    first = second = None                      # the largest and the second largest compact extra of any member
    for i in range(stack.shape[0]):
        beyond = np.clip(med - stack[i], 0.0, None)
        e = beyond - grey_opening(beyond, size=(OPENING, OPENING))
        if off in ("med", "med2"):
            if first is None:
                first, second = e.copy(), np.zeros_like(e)
            else:
                second = np.maximum(second, np.minimum(first, e))
                first = np.maximum(first, e)
        if i in trusted:
            extra_trusted = e if extra_trusted is None else np.maximum(extra_trusted, e)
        if i in pair:
            extra_pair = e if extra_pair is None else np.minimum(extra_pair, e)
    on = med - np.maximum(extra_trusted, extra_pair)
    if off == "low":
        away = stack.min(axis=0)
    else:
        away = med - (first if off == "med" else second)
    return (weight * on + (1.0 - weight) * away).astype(np.float32)


def combine(master, plates, pair, trusted, off):
    """`master` and every plate are arrays of one shape, 2-D or (channels, height, width); NaN marks an absent pixel.
    Returns the combined plate, the nebula weight and the bright nebula's share of the frame."""
    x = np.asarray(master, np.float32)
    lum = np.nan_to_num(x if x.ndim == 2 else x[:3].mean(axis=0))
    weight, share = nebula_weight(lum, lum != 0.0)     # an absent pixel is NaN or an exact zero (the ring)
    planes = range(1) if x.ndim == 2 else range(x.shape[0])
    out = []
    for c in planes:
        xc = x if x.ndim == 2 else x[c]
        bad = ~np.isfinite(xc)
        stack = np.empty((len(plates),) + xc.shape, np.float32)
        for i, p in enumerate(plates):
            q = np.asarray(p if x.ndim == 2 else p[c], np.float32)
            bad |= ~np.isfinite(q)
            stack[i] = np.where(np.isfinite(q), q, np.where(np.isfinite(xc), xc, 0.0))
        o = combine_plane(stack, pair, trusted, weight, off)
        first = np.asarray(plates[0] if x.ndim == 2 else plates[0][c], np.float32)
        o[bad] = first[bad]            # an absent pixel stays what the first plate says it is
        out.append(o)
    return (out[0] if x.ndim == 2 else np.stack(out)), weight, share


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("out"); p.add_argument("master")
    p.add_argument("--off", choices=("low", "med", "med2"), required=True)
    p.add_argument("--any", nargs="+", required=True)
    p.add_argument("--pair", nargs=2, required=True)
    p.add_argument("--trusted", nargs="+", required=True)
    p.add_argument("--mask-out")
    a = p.parse_args()
    paths = a.any + a.pair + a.trusted
    pair = {len(a.any), len(a.any) + 1}
    trusted = set(range(len(a.any) + 2, len(paths)))
    with fits.open(a.master) as h:
        master = h[0].data.astype(np.float32)
    with fits.open(paths[0]) as h:
        hdr = h[0].header.copy()
    handles = [fits.open(q, memmap=True) for q in paths]
    for q, h in zip(paths, handles):
        if h[0].data.shape != master.shape:
            raise SystemExit(f"{q}: shape {h[0].data.shape}, the master {master.shape}")
    data, weight, share = combine(master, [h[0].data for h in handles], pair, trusted, a.off)
    if a.mask_out:
        fits.PrimaryHDU(weight.astype(np.float32)).writeto(a.mask_out, overwrite=True)
    fits.PrimaryHDU(data, header=hdr).writeto(a.out, overwrite=True)
    print(f"{a.out}: {len(paths)} members, off the nebula '{a.off}', {100 * share:.1f} % of the frame is bright nebula, "
          f"median {float(np.nanmedian(data)):.6f}")
