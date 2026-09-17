"""Same target, four filters: is group M's 2024-02-03 ASI294MC night broadband? READ ONLY.

A flat's channel ratios describe a filter only on ONE sensor (the CFA response differs between
sensors), so group M cannot be compared with the 3 nm L-Ultimate on the ASI533 that way. What
transfers much better is a RATIO OF RATIOS inside one frame: the nebula's colour against the stars'
colour. Both pass through the same CFA and the same filter, so the sensor's response cancels to first
order, and what is left is the spectrum difference between emission lines and a stellar continuum,
which a filter changes enormously: a 3 nm Ha/OIII filter throws away nearly all continuum, so the
nebula comes out far redder than the stars; a broadband filter keeps it, so the gap is small.

Per frame, on the raw CFA split into half-resolution R, G, B planes (BAYERPAT from the header):
  starless    9x9 median filter of each plane
  stars       G - starless_G above 25x the plane's MAD, dilated by 2 px
  star colour sum(R - starless_R) / sum(G - starless_G) over the star mask (and B likewise)
  nebula      the brightest 2% of starless G above its own p10 sky, outside the star mask
  nebula colour median(starless_R - skyR) / median(starless_G - skyG) there (and B likewise)
Background is subtracted locally on both sides, so no bias, dark or flat is needed for a ratio.
"""
import glob
import numpy as np
import astropy.io.fits as fits
from scipy.ndimage import median_filter, binary_dilation

ORG = 'D:/Astro-Organized/lights'
SETS = [
    ('ASI294MC  UV/IR cut (group M, 2024)', sorted(glob.glob(
        'D:/Astro-Pics/2024/2024-02-03/eta Car/2024-02-03/Light/12_48_52Z/rawframes/*.fits'))),
    ('ASI585MC  L-eNhance (2025-01-14)', sorted(glob.glob(ORG + '/ZWO-ASI585MC-Pro/Optolong-L-eNhance/eta-Car-Nebula/2025-01-14/*.fits'))),
    ('SV605CC   L-Quad Enhance (2025-12-17)', sorted(glob.glob(ORG + '/SVBONY-SV605CC/Optolong-L-Quad-Enhance/eta-Car-Nebula/2025-12-17/*.fits'))),
    ('ASI533MC  L-Ultimate 3nm (2026-02-20)', sorted(glob.glob(ORG + '/ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/eta-Car-Nebula/2026-02-20/*.fits'))),
]


def split(a, pattern):
    """Any 2x2 Bayer order: the four cells in reading order are pattern[0..3]."""
    a = a[:a.shape[0] // 2 * 2, :a.shape[1] // 2 * 2]
    quads = (a[0::2, 0::2], a[0::2, 1::2], a[1::2, 0::2], a[1::2, 1::2])
    by = {}
    for c, q in zip(pattern, quads):
        by.setdefault(c, []).append(q)
    assert len(by.get('R', [])) == 1 and len(by.get('B', [])) == 1 and len(by.get('G', [])) == 2, pattern
    return by['R'][0], 0.5 * (by['G'][0] + by['G'][1]), by['B'][0]


def measure(path):
    h = fits.getheader(path)
    pat = str(h.get('BAYERPAT', '')).strip().upper()
    a = fits.getdata(path).astype(np.float32)
    r, g, b = split(a, pat)
    sl = [median_filter(x, size=9) for x in (r, g, b)]
    mad = np.median(np.abs(g - np.median(g)))
    stars = binary_dilation((g - sl[1]) > 25 * mad, iterations=2)
    exc = [x - s for x, s in zip((r, g, b), sl)]
    star_rg = exc[0][stars].sum() / exc[1][stars].sum()
    star_bg = exc[2][stars].sum() / exc[1][stars].sum()
    sky = [np.percentile(s, 10) for s in sl]
    # Chosen on ALL THREE channels, each in its own noise units: choosing on green alone picks the
    # OIII-bright gas and makes an Ha/OIII filter look bluer than its stars (L-eNhance read 0.45).
    noise = [np.median(np.abs(s - np.median(s))) + 1e-6 for s in sl]
    lift = sum((s - k) / n for s, k, n in zip(sl, sky, noise))
    neb = (lift > np.percentile(lift, 98)) & ~stars
    neb_rg = np.median(sl[0][neb] - sky[0]) / np.median(sl[1][neb] - sky[1])
    neb_bg = np.median(sl[2][neb] - sky[2]) / np.median(sl[1][neb] - sky[1])
    return pat, star_rg, star_bg, neb_rg, neb_bg, int(stars.sum())


print(f"{'set':40s} {'bayer':5s} {'star R/G':>8s} {'neb R/G':>8s} {'CONTRAST R':>10s} {'star B/G':>8s} {'neb B/G':>8s} {'CONTRAST B':>10s}")
for label, ps in SETS:
    if not ps:
        print(f'{label}: no frames found')
        continue
    rows = [measure(ps[i]) for i in np.linspace(0, len(ps) - 1, 3).round().astype(int)]
    pat = rows[0][0]
    m = np.median(np.array([x[1:] for x in rows]), axis=0)
    print(f'{label:40s} {pat:5s} {m[0]:8.3f} {m[2]:8.3f} {m[2] / m[0]:10.2f} {m[1]:8.3f} {m[3]:8.3f} {m[3] / m[1]:10.2f}')
