"""A no-reference readout for an EXTENDED object: the Bubble Nebula's shell as a line-spread function.

The star readouts need a sharper half of the same night as the truth; a nebula has no such truth on
a single master. The Bubble (NGC 7635) offers its own: a thin, nearly circular shell, whose radial
profile is a line spread function. Read on the stretched luminance:

- rim width: FWHM in RADIUS of the shell's peak in the azimuthally-medianed radial profile, the
  extended-object analogue of a star's FWHM (tighter is sharper);
- rim overshoot: the profile's minimum just OUTSIDE the rim relative to the far background, in units
  of the exterior noise (a dip below it is ringing, exactly the moat a star gets);
- interior and exterior noise: MAD of the high-passed luminance inside and outside the shell, so
  noise amplification is read where there is no edge.

The centre and radius are fitted from a guess by maximising the rim's annulus mean over its
surroundings, with stars masked, on a window round the guess. Usage:

    python n2n_rim_readout.py --fits flat.fits deconv.fits --centre 1981,1098 --radius 187
"""
import argparse

import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter, binary_dilation, maximum_filter

import n2n_operator as OP


def rd(p):
    with fits.open(p, memmap=False) as h:
        d = np.nan_to_num(np.asarray(next(x for x in h if x.data is not None).data, dtype=np.float32))
    return d if d.ndim == 3 else d[None]


def stretch_lum(d, params=None):
    peak = float(d.max()); div = peak if peak > 1.0 else 1.0
    if params is None:
        params = OP.session_stretch_params(d, div)
    mins, betas = params
    u = d / div
    s = np.stack([OP.mtf(betas[c], np.clip(u[c] - mins[c], 0, None).astype(np.float64)) for c in range(d.shape[0])])
    return s.mean(axis=0).astype(np.float32), params


def star_mask(lum, sigma=8.0, grow=6):
    med = float(np.median(lum)); mad = 1.4826 * float(np.median(np.abs(lum - med)))
    peaks = (lum == maximum_filter(lum, size=5)) & (lum > med + sigma * mad)
    yy, xx = np.mgrid[-grow:grow + 1, -grow:grow + 1]
    return binary_dilation(peaks, structure=(yy * yy + xx * xx) <= grow * grow)


def radial_profile(win, mask, cx, cy, rmax, step=1.0):
    """Azimuthal MEDIAN per 1 px annulus, vectorised: sort by radius, split at the bin edges."""
    h, w = win.shape
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2).ravel()
    v = win.ravel()
    keep = (~mask.ravel()) & (r < rmax)
    r, v = r[keep], v[keep]
    order = np.argsort(r)
    r, v = r[order], v[order]
    edges = np.arange(0, rmax + step, step)
    idx = np.searchsorted(r, edges)
    prof = np.full(len(edges) - 1, np.nan)
    for i in range(len(edges) - 1):
        seg = v[idx[i]:idx[i + 1]]
        if seg.size > 20:
            prof[i] = np.median(seg)
    return 0.5 * (edges[:-1] + edges[1:]), prof


def fit_circle(win, mask, cx, cy, r0, search=12):
    """Centre and radius that maximise the rim's annulus mean over the ring 15 to 28 px outside it."""
    best = None
    for dy in range(-search, search + 1, 3):
        for dx in range(-search, search + 1, 3):
            rr, prof = radial_profile(win, mask, cx + dx, cy + dy, r0 + 40, step=2.0)
            for dr in range(-10, 11, 2):
                sel = np.abs(rr - (r0 + dr)) <= 3
                out = (rr > r0 + dr + 15) & (rr < r0 + dr + 28)
                score = np.nanmean(prof[sel]) - np.nanmean(prof[out])
                if best is None or score > best[0]:
                    best = (score, cx + dx, cy + dy, r0 + dr)
    return best[1], best[2], best[3]


def read(lum, cx, cy, r0, fit=True):
    pad = int(r0 + 140)
    x0, y0 = int(cx - pad), int(cy - pad)
    win = lum[max(y0, 0):int(cy + pad), max(x0, 0):int(cx + pad)]
    lx, ly = cx - max(x0, 0), cy - max(y0, 0)
    mask = star_mask(win)
    if fit:
        lx, ly, r0 = fit_circle(win, mask, lx, ly, r0)
    # Per SECTOR, because the shell is neither circular nor evenly bright: an azimuthal median over
    # the whole ring reads the ellipticity as width (48 px on the first try). Each 10-degree sector
    # gets its own radial profile, its own rim peak within 25 px of the fitted radius, and its own
    # half-maximum width over the higher of its interior and far levels; the medians over the
    # sectors whose rim stands 3 exterior sigmas over that base are the readout.
    hp = win - uniform_filter(win, 7)
    h, w = win.shape
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((xx - lx) ** 2 + (yy - ly) ** 2)
    theta = np.arctan2(yy - ly, xx - lx)
    ext = (r > r0 + 40) & (r < r0 + 120) & ~mask
    inn = (r < r0 * 0.6) & ~mask
    mad = lambda a: 1.4826 * float(np.median(np.abs(a - np.median(a))))
    sigma_out = max(mad(hp[ext]), 1e-9)
    widths, dips, contrasts, peaks = [], [], [], []
    n_sectors = 36
    for k in range(n_sectors):
        t0, t1 = -np.pi + k * 2 * np.pi / n_sectors, -np.pi + (k + 1) * 2 * np.pi / n_sectors
        sector = (theta >= t0) & (theta < t1)
        sub_mask = mask | ~sector
        rr, prof = radial_profile(win, sub_mask, lx, ly, r0 + 80, step=1.0)
        far = np.nanmedian(prof[(rr > r0 + 40) & (rr < r0 + 80)])
        inner = np.nanmedian(prof[(rr > r0 * 0.4) & (rr < r0 * 0.7)])
        band = (rr > r0 - 25) & (rr < r0 + 25)
        if not np.isfinite(prof[band]).any():
            continue
        ip = int(np.nanargmax(np.where(band, prof, -np.inf)))
        peak_r, peak = rr[ip], prof[ip]
        base = max(far, inner)
        if not np.isfinite(base) or peak - base < 3.0 * sigma_out:
            continue
        half = base + 0.5 * (peak - base)
        lo = ip
        while lo > 0 and prof[lo] > half:
            lo -= 1
        hi = ip
        while hi < len(prof) - 1 and prof[hi] > half:
            hi += 1
        widths.append(rr[hi] - rr[lo])
        out = (rr > peak_r + 4) & (rr < peak_r + 30)
        dips.append((np.nanmin(prof[out]) - far) / sigma_out)
        contrasts.append(peak - base)
        peaks.append(peak_r)
    if not widths:
        return dict(cx=lx + max(x0, 0), cy=ly + max(y0, 0), r=r0, peak_r=float("nan"), width=float("nan"),
                    contrast=float("nan"), dip_sigma=float("nan"), noise_out=sigma_out, noise_in=mad(hp[inn]), sectors=0)
    return dict(cx=lx + max(x0, 0), cy=ly + max(y0, 0), r=r0, peak_r=float(np.median(peaks)),
                width=float(np.median(widths)), contrast=float(np.median(contrasts)), dip_sigma=float(np.median(dips)),
                noise_out=sigma_out, noise_in=mad(hp[inn]), sectors=len(widths))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--fits", nargs="+", required=True, help="first is the reference for the stretch and the circle fit")
    p.add_argument("--centre", default="1981,1098")
    p.add_argument("--radius", type=float, default=187)
    p.add_argument("--labels", default=None, help="comma list, one per fits")
    p.add_argument("--scale", type=float, default=1.0, help="the fits are at this scale of the first (e.g. 0.5): centre and radius scale")
    a = p.parse_args()
    cx, cy = (float(v) for v in a.centre.split(","))
    labels = a.labels.split(",") if a.labels else [f.split("/")[-1] for f in a.fits]
    params = None
    first = None
    print(f"{'arm':28s} {'rim width px':>12} {'contrast':>9} {'dip (sigma)':>11} {'noise out':>9} {'noise in':>8}   circle")
    for f, lab in zip(a.fits, labels):
        lum, params = stretch_lum(rd(f), params)
        if first is None:
            r = read(lum, cx, cy, a.radius, fit=True)
            first = r
        else:
            r = read(lum, first["cx"], first["cy"], first["r"], fit=False)
        print(f"{lab:28s} {r['width']:12.1f} {r['contrast'] * 1e3:9.2f} {r['dip_sigma']:+11.2f} {r['noise_out'] * 1e3:9.3f} {r['noise_in'] * 1e3:8.3f}   "
              f"({r['cx']:.0f},{r['cy']:.0f}) r {r['r']:.0f} peak at {r['peak_r']:.0f}, {r['sectors']} sectors")
    print("\nread: rim width is the shell's FWHM in radius on the stretched luminance (tighter is sharper); contrast the rim's "
          "height over the higher of interior and far background (1e-3 stretched units); dip the minimum just outside the rim "
          "against the far background in exterior-noise sigmas (under about -2 is ringing); noise is the 7 px high-pass MAD "
          "outside and inside the shell (1e-3), amplification reads there.")


if __name__ == "__main__":
    main()
