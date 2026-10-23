"""Andromeda's bright isolated dark-sky stars: per-channel radial profile of the input and of each model's residual
(starless minus the local sky), in units of that channel's sky noise, and each channel's FWHM. Glue for a diagnosis."""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import maximum_filter, uniform_filter, median_filter

inp = fits.getdata(sys.argv[1]).astype(np.float32)          # (3, H, W)
plates = [(a.split('=')[0], fits.getdata(a.split('=')[1]).astype(np.float32)) for a in sys.argv[2:]]
C, H, W = inp.shape
g = inp[1]
bg = median_filter(g[::4, ::4], size=33)
bg = np.kron(bg, np.ones((4, 4), np.float32))[:H, :W]
sig = []
for c in range(3):
    d = inp[c] - np.roll(inp[c], 1, 1)
    sig.append(1.4826 * np.median(np.abs(d - np.median(d))) / np.sqrt(2))
sig = np.array(sig)
dark = bg < np.percentile(bg, 40)
peak = (g == maximum_filter(g, 31)) & dark
snr = (g - bg) / sig[1]
ys, xs = np.nonzero(peak & (snr > 100) & (snr < 1000))
keep = [(y, x) for y, x in zip(ys, xs) if 20 <= y < H - 20 and 20 <= x < W - 20 and inp[:, y, x].max() < 0.95]
print(f'{len(keep)} bright isolated dark-sky stars; sky noise R/G/B {sig[0]:.2e} {sig[1]:.2e} {sig[2]:.2e}')
R = 12
yy, xx = np.mgrid[-R:R + 1, -R:R + 1]
rr = np.hypot(yy, xx)
bins = [(0, 1.5), (1.5, 3), (3, 4.5), (4.5, 6), (6, 9), (9, 12)]
def prof(img, c, y, x):
    cut = img[c, y - R:y + R + 1, x - R:x + R + 1]
    sky = np.nanmedian(cut[rr > 10.5])
    return [np.nanmean(cut[(rr >= a) & (rr < b)] - sky) / sig[c] for a, b in bins]
def fwhm(c, y, x):
    cut = inp[c, y - R:y + R + 1, x - R:x + R + 1]
    sky = np.median(cut[rr > 10.5]); pk = cut[R, R] - sky
    return 2 * np.sqrt(((cut - sky) > pk / 2).sum() / np.pi)
print('bins (px): ' + '  '.join(f'{a}-{b}' for a, b in bins))
for c, n in enumerate('RGB'):
    f = np.median([fwhm(c, y, x) for y, x in keep])
    p = np.median([prof(inp, c, y, x) for y, x in keep], 0)
    print(f'input {n}: FWHM {f:.2f} px; profile ' + ' '.join(f'{v:8.1f}' for v in p))
for name, pl in plates:
    for c, n in enumerate('RGB'):
        p = np.nanmedian([prof(pl, c, y, x) for y, x in keep], 0)
        print(f'{name:>8} {n} residual: ' + ' '.join(f'{v:6.2f}' for v in p))
