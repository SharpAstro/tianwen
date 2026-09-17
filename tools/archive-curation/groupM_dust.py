"""Group M: does the next-morning flat carry dust, and does the night's sky show the same dust?
If the flat has none, it only corrects vignetting and a moved lens costs little; if it has motes, they
must sit where the lights' own motes sit. READ ONLY."""
import glob
import numpy as np
import astropy.io.fits as fits
from scipy.ndimage import gaussian_filter, median_filter

ROOT = r'D:/Astro-Pics/2024/2024-02-03'
FLATS = sorted(glob.glob(ROOT + '/Flats/*.fits'))
DFLATS = sorted(glob.glob(ROOT + '/DarkFlats/2024-02-03/DarkFlat/22_55_28Z/*.fits'))
LIGHTS = sorted(glob.glob(ROOT + '/eta Car/2024-02-03/Light/12_48_52Z/rawframes/*.fits'))
DARKS = sorted(glob.glob(ROOT + '/eta Car Darks/2024-02-03/Light/15_34_37Z/*.fits'))


def green(a):
    return 0.5 * (a[0::2, 1::2] + a[1::2, 0::2])


def med(ps, n):
    idx = np.linspace(0, len(ps) - 1, n).round().astype(int)
    return np.median(np.stack([fits.getdata(ps[i]).astype(np.float32) for i in idx]), axis=0)


flat = green(med(FLATS, 20) - med(DFLATS, 20))
# Detrend on ~50 px of the half-resolution green plane (100 raw px): keeps motes (tens of px), drops vignetting.
f_rel = flat / gaussian_filter(flat, 25)
print(f'flat compact structure: p0.1 {np.percentile(f_rel, 0.1):.4f}  p1 {np.percentile(f_rel, 1):.4f}  '
      f'pixels below 0.97: {(f_rel < 0.97).mean():.4%}')

# The lights' sky with the stars suppressed: median of 40 frames spread over 2.7 h (the field drifts on an
# untracked-by-dither mount only a little, so a median alone keeps some stars) then a 5x5 median filter.
sky = green(med(LIGHTS, 40) - med(DARKS, 20))
sky = median_filter(sky, size=5)
s_rel = sky / gaussian_filter(sky, 25)
print(f'lights sky structure:   p0.1 {np.percentile(s_rel, 0.1):.4f}  p1 {np.percentile(s_rel, 1):.4f}  '
      f'pixels below 0.97: {(s_rel < 0.97).mean():.4%}')

# Where the flat's darkest compact features are, does the sky dip too?
mask = f_rel < np.percentile(f_rel, 0.1)
print(f'sky at the flat\'s darkest 0.1%: median {np.median(s_rel[mask]):.4f} (1.0000 = no matching dip)')
