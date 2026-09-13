"""Star SHAPE statistics for a deconvolution arm, beside the gate's width and ring depth.

`n2n_deconv_gate.ring_excess` is a depth: the annulus MINIMUM under the background, in MAD, and it
moves the right way for Richardson-Lucy's noise amplification. It cannot see what the eye called a
"black square inside the halo" on the E3.2 prior, because that is not a dip under the sky: it is a
star whose skirt was taken away while its far halo stayed, a profile too STEEP against the truth.
Three statistics that can see it, all read on the truth's star list:

- `skirt_ratio`: the output's mean annulus level at one truth-FWHM out, over the truth's, both as a
  fraction of their own peak. 1.0 is the truth's profile; under 1.0 the skirt is gone (block and moat);
  over 1.0 it is still blurred.
- `ring_mean`: the annulus MEAN over background in MAD (the gate's ring geometry), signed, so a bright
  ring reads positive and a moat negative, and neither hides the other in a min.
- `profile_misfit`: RMS residual of a circular Moffat fit (beta 4, the synthetic convention) over a
  9 by 9 patch, as a fraction of the fitted peak. A round star fits; a two-pixel block, a moat or a
  carved pixel do not, whatever their width reads.
"""
import numpy as np
from scipy.optimize import least_squares

import n2n_deconv_gate as DG


def _annulus(r_in, r_out):
    r = int(np.ceil(r_out))
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    dist = np.sqrt(yy * yy + xx * xx)
    return r, (dist >= r_in) & (dist <= r_out)


def _patches(tile, ys, xs, r):
    h, w = tile.shape
    for y, x in zip(ys, xs):
        if y - r < 0 or x - r < 0 or y + r >= h or x + r >= w:
            continue
        yield tile[y - r:y + r + 1, x - r:x + r + 1]


def skirt_level(tile, ys, xs, fwhm_truth, med):
    """Per star: mean of the annulus at [1.0, 1.5] truth FWHM over the peak (both background-subtracted)."""
    r, ring = _annulus(1.0 * fwhm_truth, 1.5 * fwhm_truth)
    out = []
    for p in _patches(tile, ys, xs, r):
        peak = p.max() - med
        if peak <= 0:
            continue
        out.append((p[ring].mean() - med) / peak)
    return np.asarray(out, dtype=np.float64)


def skirt_ratio(tile, truth, ys, xs, fwhm_truth, med_tile, med_truth):
    a, b = skirt_level(tile, ys, xs, fwhm_truth, med_tile), skirt_level(truth, ys, xs, fwhm_truth, med_truth)
    n = min(len(a), len(b))
    if n == 0:
        return float("nan")
    return float(np.median(a[:n]) / np.median(b[:n]))


def ring_mean(tile, ys, xs, fwhm, med, mad):
    """Signed annulus mean over background in MAD, the gate's ring geometry, median over stars."""
    r, ring = _annulus(DG.RING_INNER * fwhm, DG.RING_OUTER * fwhm)
    vals = [(p[ring].mean() - med) / mad for p in _patches(tile, ys, xs, r)]
    return float(np.median(vals)) if vals else float("nan")


def _moffat(params, yy, xx, beta=4.0):
    amp, y0, x0, alpha, bg = params
    rr = ((yy - y0) ** 2 + (xx - x0) ** 2) / (alpha * alpha)
    return bg + amp * (1.0 + rr) ** (-beta)


def profile_misfit(tile, ys, xs, med, fwhm_guess, max_stars=400, half=4):
    """Median over stars of the Moffat fit's RMS residual as a fraction of the fitted peak."""
    yy, xx = np.mgrid[-half:half + 1, -half:half + 1].astype(np.float64)
    alpha0 = max(0.5, fwhm_guess / (2.0 * np.sqrt(2.0 ** (1.0 / 4.0) - 1.0)))
    out = []
    for p in _patches(tile, ys, xs, half):
        if len(out) >= max_stars:
            break
        p = p.astype(np.float64)
        peak = p.max() - med
        if peak <= 0:
            continue
        x0 = [peak, 0.0, 0.0, alpha0, med]
        try:
            fit = least_squares(lambda q: (_moffat(q, yy, xx) - p).ravel(), x0,
                                bounds=([0, -2, -2, 0.2, -np.inf], [np.inf, 2, 2, 20, np.inf]), max_nfev=200)
        except ValueError:
            continue
        out.append(np.sqrt(np.mean(fit.fun ** 2)) / max(fit.x[0], 1e-9))
    return float(np.median(out)) if out else float("nan")


def shape_row(tile, truth, ys, xs, fwhm_truth, med_tile, mad_tile, med_truth):
    """(skirt ratio, signed ring mean in MAD, profile misfit) for one arm on the truth's stars."""
    return (skirt_ratio(tile, truth, ys, xs, fwhm_truth, med_tile, med_truth),
            ring_mean(tile, ys, xs, fwhm_truth, med_tile, mad_tile),
            profile_misfit(tile, ys, xs, med_tile, fwhm_truth))
