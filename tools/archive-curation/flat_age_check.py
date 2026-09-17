"""Has the dust moved between the 2025-11-03 flat and the 2026-08-01 lights?

The test is direct rather than inferential: find where the FLAT dips (a dust mote is a
compact shadow), then ask whether the LIGHT dips in the same places by a comparable
relative amount. Dust still on the window shadows both; dust that has moved shadows only
the flat, and dividing by that flat would then PAINT a bright ring where nothing is.

Both sides are reduced to a 2x2 block mean first, which removes the CFA without debayering
(BAYERPAT here is GRBG, and sampling the wrong phase is its own trap), then divided by a
heavily smoothed copy of themselves so only compact structure survives. Vignetting is
low-frequency and cancels; a nebula is large and smooth compared with a mote.

READ ONLY.
"""
import glob
import numpy as np
import astropy.io.fits as fits
from scipy import ndimage

FLATDIR = r'D:/Astro-Organized/flats/SVBONY-SV605CC/Optolong-L-Ultimate-3nm/2025-11-03/FLAT'
DARKFLATDIR = r'D:/Astro-Organized/flats/SVBONY-SV605CC/Optolong-L-Ultimate-3nm/2025-11-03/DARKFLAT'
LIGHTDIR = r'D:/Astro-Pics/2026/2026-08-01 SH61 L-Ultimate Lagoon + SMC/LIGHT'

SMOOTH = 40          # block-mean pixels; motes are much smaller than this
MOTE_DEPTH = 0.015   # a dip of >1.5 percent counts as a mote


def block_mean(a):
    h, w = a.shape[0] // 2 * 2, a.shape[1] // 2 * 2
    return a[:h, :w].reshape(h // 2, 2, w // 2, 2).mean(axis=(1, 3))


def compact(a):
    """Compact structure only: the frame over a heavily smoothed copy of itself."""
    sm = ndimage.gaussian_filter(a, SMOOTH, mode='nearest')
    return a / np.maximum(sm, 1e-6)


def stack_median(paths, n):
    arrs = []
    for p in paths[:n]:
        arrs.append(block_mean(fits.getdata(p).astype(np.float32)))
    return np.median(np.stack(arrs), axis=0)


flats = sorted(glob.glob(FLATDIR + '/*.fits'))
darkflats = sorted(glob.glob(DARKFLATDIR + '/*.fits'))
lights = sorted(glob.glob(LIGHTDIR + '/*.fits'))
print(f'flats {len(flats)}  darkflats {len(darkflats)}  lights {len(lights)}')

flat = stack_median(flats, 15)
if darkflats:
    flat = flat - stack_median(darkflats, 15)
light = stack_median(lights, 25)

fc = compact(flat)
lc = compact(light)

# Where does the FLAT dip? Those are its dust motes.
motes = fc < (1.0 - MOTE_DEPTH)
motes = ndimage.binary_opening(motes, np.ones((3, 3)))
lbl, n = ndimage.label(motes)
print(f'dust motes found in the 2025-11-03 flat: {n}')

rows = []
for i in range(1, n + 1):
    m = lbl == i
    size = int(m.sum())
    if size < 25:
        continue
    fd = 1.0 - float(fc[m].mean())      # how deep the flat dips, as a fraction
    ld = 1.0 - float(lc[m].mean())      # how deep the light dips in the same place
    cy, cx = ndimage.center_of_mass(m)
    rows.append((size, fd, ld, cx * 2, cy * 2))

rows.sort(reverse=True)
print()
print('%6s %9s %9s %8s  %s' % ('px', 'flat dip', 'light dip', 'ratio', 'centre (full-res x,y)'))
agree = 0
for size, fd, ld, cx, cy in rows[:15]:
    ratio = ld / fd if fd > 1e-6 else float('nan')
    if ratio > 0.5:
        agree += 1
    print('%6d %8.1f%% %8.1f%% %8.2f  (%.0f, %.0f)' % (size, fd * 100, ld * 100, ratio, cx, cy))

kept = [r for r in rows if r[0] >= 25]
if kept:
    ratios = np.array([r[2] / r[1] for r in kept if r[1] > 1e-6])
    print()
    print(f'motes measured: {len(ratios)}')
    print(f'  median light/flat dip ratio: {np.median(ratios):.2f}')
    print(f'  motes where the light ALSO dips (ratio > 0.5): {int((ratios > 0.5).sum())} of {len(ratios)}')
    print()
    print('READ: ratio near 1 means the dust is still there and the flat is still valid.')
    print('      ratio near 0 means the flat has a mote the sky does not, so dividing by it')
    print('      would PAINT a bright ring into every calibrated frame.')
