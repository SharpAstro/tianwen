"""Can a remover's stars layer be screened back onto its starless without the stars blowing up? (2026-11-22, the owner:
"can we re-screen the starlight lots of the milkyway without blowing up the stars. we could test this against some of
our frames", both edits measured apart.)

    python -I screen_back.py <master fits> <check2 work dir> <label>=<starless fits> [...] [--mtf 0.3]

Display space: every image is stretched with the MASTER's own stretch, per channel (shadows at the median less 2.8
MAD, the median put at 0.25 by a midtones transfer), the starless with the master's parameters, so the two stay on one
scale. The stars layer is the UNSCREEN of the two: T = 1 - (1 - M) / (1 - S), so screening it back untouched gives M
again (checked, the error printed). Then two edits, measured apart:

  (a) the starless stretched harder (a midtones transfer of --mtf on it), the stars layer screened back unchanged;
  (b) the stars layer stretched the same way, screened back onto the original starless.

The reference row is the whole master given edit (a)'s stretch directly, no star removal at all.

Measured on isolated catalogued stars (the scorer's detector on green: compact, peak SNR 20 to 2,000, no other source
of any channel within 12 px, 24 px from the edge): the half-flux diameter of the star above its own 10 to 16 px annulus,
as a median ratio to the same star in the stretched master; the share of stars whose peak clips (0.98 or over in any
channel), beside the master's own share; and the FOG: the median, over pixels farther than 6 px from every catalogued
source, of what the screen added on top of the edited starless (result less the starless it was screened onto), in
the master's display-space sky noise.
"""
import csv
import glob
import os
import sys

import numpy as np
from astropy.io import fits
from scipy.ndimage import distance_transform_edt

args = [a for a in sys.argv[1:]]
mtf_m = 0.3
if "--mtf" in args:
    i = args.index("--mtf")
    mtf_m = float(args[i + 1])
    del args[i:i + 2]
MASTER, WORK = args[0], args[1]
REMOVERS = [a.split("=", 1) for a in args[2:]]


def planes(path):
    a = np.asarray(fits.getdata(path), np.float64)
    if a.ndim == 2:
        a = np.stack([a, a, a])
    return np.nan_to_num(a[:3])


def mtf(m, x):
    return ((m - 1) * x) / (((2 * m - 1) * x) - m)


def stretch_params(c):
    med = float(np.median(c))
    mad = 1.4826 * float(np.median(np.abs(c - med)))
    lo = med - 2.8 * mad
    hi = float(np.max(c))
    x = (med - lo) / (hi - lo)
    # The midtones balance that maps x to 0.25: solve mtf(m, x) = 0.25.
    t = 0.25
    m = (x * (t - 1)) / ((2 * t * x) - t - x)
    return lo, hi, m


def stretch(c, p):
    lo, hi, m = p
    return mtf(m, np.clip((c - lo) / (hi - lo), 0, 1))


def screen(a, b):
    return 1 - (1 - a) * (1 - b)


def unscreen(m, s):
    return np.clip(1 - (1 - m) / np.maximum(1 - s, 1e-6), 0, 1)


def stars_list():
    rows = {}
    for path in glob.glob(os.path.join(WORK, "*_c*.sources.csv")):
        ch = path.rsplit("_c", 1)[1][0]
        rows[ch] = list(csv.DictReader(open(path, encoding="utf-8")))
    allxy = np.array([(float(r["x"]), float(r["y"])) for rs in rows.values() for r in rs])
    green = rows.get("1") or next(iter(rows.values()))
    out = []
    for r in green:
        if r["compact"] != "1":
            continue
        snr = float(r["peak_snr"])
        if not 20 <= snr <= 2000:
            continue
        x, y = float(r["x"]), float(r["y"])
        d = np.hypot(allxy[:, 0] - x, allxy[:, 1] - y)
        # The star itself appears once per channel within 1.5 px; any other source within 12 px makes it not isolated.
        if np.any((d > 1.5) & (d < 12)):
            continue
        out.append((x, y))
    return out, allxy


def hfd_and_peak(lum, x, y):
    xi, yi = int(round(x)), int(round(y))
    cut = lum[yi - 16:yi + 17, xi - 16:xi + 17]
    yy, xx = np.mgrid[-16:17, -16:17]
    r = np.hypot(xx - (x - xi), yy - (y - yi))
    bg = np.median(cut[(r >= 10) & (r <= 16)])
    v = np.clip(cut - bg, 0, None) * (r <= 9)
    tot = v.sum()
    if tot <= 0:
        return np.nan
    order = np.argsort(r.ravel())
    cum = np.cumsum(v.ravel()[order])
    return 2 * r.ravel()[order][np.searchsorted(cum, tot / 2)]


def measure(img3, stars, ref_hfd, far, sky_noise, base3=None):
    lum = img3.mean(axis=0)
    h = np.array([hfd_and_peak(lum, x, y) for x, y in stars])
    ok = np.isfinite(h) & np.isfinite(ref_hfd) & (ref_hfd > 0)
    clip = np.mean([img3[:, int(round(y)), int(round(x))].max() >= 0.98 for x, y in stars])
    fog = float(np.median((img3 - base3).mean(axis=0)[far]) / sky_noise) if base3 is not None else float("nan")
    return float(np.median(h[ok] / ref_hfd[ok])), clip, fog


M = planes(MASTER)
params = [stretch_params(M[c]) for c in range(3)]
Ms = np.stack([stretch(M[c], params[c]) for c in range(3)])
H, W = Ms.shape[1:]
stars, allxy = stars_list()
stars = [(x, y) for x, y in stars if 24 <= x < W - 24 and 24 <= y < H - 24]
mask = np.zeros((H, W), bool)
for x, y in allxy:
    xi, yi = int(round(x)), int(round(y))
    if 0 <= xi < W and 0 <= yi < H:
        mask[yi, xi] = True
# The zero canvas ring (planes() turns NaN into 0) is not sky: it is out of `far`, so no figure is measured on it.
far = (distance_transform_edt(~mask) > 6) & (M != 0).any(axis=0)
lum_m = Ms.mean(axis=0)
# The noise unit: neighbouring-pixel differences along rows over a STRIDED sample of the whole frame, so it does not
# depend on which rows the first 200,000 far pixels fall in. Both pixels of a pair must be far.
pair = far[:, 1:] & far[:, :-1]
py, px = pair.nonzero()
step = max(1, len(py) // 200000)
py, px = py[::step], px[::step]
sky_noise = 1.4826 * float(np.median(np.abs(lum_m[py, px + 1] - lum_m[py, px]))) / np.sqrt(2)
if not np.isfinite(sky_noise) or sky_noise <= 0:
    raise SystemExit("no sky noise: no far pixel pair outside the zero canvas ring")
ref_hfd = np.array([hfd_and_peak(lum_m, x, y) for x, y in stars])
print(f"{os.path.basename(MASTER)}: {len(stars)} isolated stars, their HFD in the stretched master median {np.nanmedian(ref_hfd):.2f} px; "
      f"{100 * np.mean([Ms[:, int(round(y)), int(round(x))].max() >= 0.98 for x, y in stars]):.1f} % of their peaks clip in the master; "
      f"edit stretch: a midtones transfer of {mtf_m:g}")
whole = mtf(mtf_m, Ms)
hr, cl, _ = measure(whole, stars, ref_hfd, far, sky_noise)
print(f"  {'no removal: the whole master stretched (a)':52s} HFD x{hr:5.2f}, clipped {100 * cl:5.1f} %")
for label, path in REMOVERS:
    S = planes(path)
    Ss = np.stack([stretch(S[c], params[c]) for c in range(3)])
    T = unscreen(Ms, Ss)
    err = float(np.max(np.abs(screen(Ss, T) - Ms)))
    pedestal = float(np.median(T.mean(axis=0)[far]) / sky_noise)
    Sa = mtf(mtf_m, Ss)
    ra = screen(Sa, T)
    hr_a, cl_a, fog_a = measure(ra, stars, ref_hfd, far, sky_noise, Sa)
    Tb = mtf(mtf_m, T)
    rb = screen(Ss, Tb)
    hr_b, cl_b, fog_b = measure(rb, stars, ref_hfd, far, sky_noise, Ss)
    print(f"  {label:20s} stars layer between the stars {pedestal:5.2f} sky noise (screen-back error {err:.1e})")
    print(f"  {'':20s} (a) starless stretched, stars back: HFD x{hr_a:5.2f}, clipped {100 * cl_a:5.1f} %, fog {fog_a:5.2f}")
    print(f"  {'':20s} (b) stars stretched, back on starless: HFD x{hr_b:5.2f}, clipped {100 * cl_b:5.1f} %, fog {fog_b:5.2f}")
