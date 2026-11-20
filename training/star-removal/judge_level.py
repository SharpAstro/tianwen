"""A diagnostic (2026-11-20): our plate with ONLY the judge's level taken, at about 100 px and over.
    python -I judge_level.py <our starless fits> <judge's starless fits> <out fits>
out = ours - L, where L is (ours less the judge) block-averaged 4 x 4, through a 25 px median there (100 px on the frame)
and interpolated back. Nothing under about 50 px changes, so the grain and every compact difference stay ours. Beside
judge_broad.py's 15 and 31 px plates it says how much of a row is the level and how much is structure of 15 to 100 px.
Prints the level taken in the plate's pixel sigma."""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter, zoom

OURS, JUDGE, OUT = sys.argv[1:4]
with fits.open(OURS) as h:
    ours = np.asarray(h[0].data, np.float32); header = h[0].header
judge = np.asarray(fits.getdata(JUDGE), np.float32)
o3 = ours[None] if ours.ndim == 2 else ours
j3 = judge[None] if judge.ndim == 2 else judge
out = o3.copy()
for c in range(o3.shape[0]):
    ok = np.isfinite(o3[c]) & np.isfinite(j3[c]) & (o3[c] != 0)
    d = np.where(ok, o3[c] - j3[c], np.nan)
    H, W = d.shape; h4, w4 = H // 4 * 4, W // 4 * 4
    small = np.nanmean(d[:h4, :w4].reshape(h4 // 4, 4, w4 // 4, 4), axis=(1, 3))
    small = np.where(np.isfinite(small), small, np.nanmedian(small))
    L = zoom(median_filter(small, 25), (H / small.shape[0], W / small.shape[1]), order=1)[:H, :W]
    out[c] = np.where(ok, o3[c] - L, o3[c])
    pair = ok[:, 1:] & ok[:, :-1]
    dx = (o3[c][:, 1:] - o3[c][:, :-1])[pair]
    sig = 1.4826 * np.median(np.abs(dx[::max(1, dx.size // 4000000)])) / np.sqrt(2)
    print(f"channel {c}: the level taken, in pixel sigma: median {np.median(L[ok]) / sig:+.2f}, 10th to 90th percentile {np.percentile(L[ok], 10) / sig:+.2f} to {np.percentile(L[ok], 90) / sig:+.2f}")
fits.PrimaryHDU(out[0] if ours.ndim == 2 else out, header).writeto(OUT, overwrite=True)
print(OUT)
