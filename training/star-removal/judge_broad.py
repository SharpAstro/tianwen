"""A diagnostic, not a product step: our plate with the BROAD light the judge takes beyond ours also taken (2026-11-20).
    python -I judge_broad.py <our starless fits> <judge's starless fits> <out fits> [median px, default 15]
out = ours - median_filter(ours - judge, px) per channel: the compact differences (a star one of the two left) stay as
they are in ours, the smooth difference goes. Scored on the whole-master scorecard it says whether the crowded rows' gap
to the judge is star removal or the judge's take of the light between the stars. Prints the broad part's size."""
import sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import median_filter

OURS, JUDGE, OUT = sys.argv[1:4]
PX = int(sys.argv[4]) if len(sys.argv) > 4 else 15
with fits.open(OURS) as h:
    ours = np.asarray(h[0].data, np.float32); header = h[0].header
judge = np.asarray(fits.getdata(JUDGE), np.float32)
o3 = ours[None] if ours.ndim == 2 else ours
j3 = judge[None] if judge.ndim == 2 else judge
out = o3.copy()
for c in range(o3.shape[0]):
    ok = np.isfinite(o3[c]) & np.isfinite(j3[c]) & (o3[c] != 0)
    diff = np.where(ok, o3[c] - j3[c], 0.0)
    broad = median_filter(diff, PX)
    out[c] = np.where(ok, o3[c] - broad, o3[c])
    # pixel sigma over the whole valid plate: the MAD of horizontal neighbour differences where both pixels are valid
    pair = ok[:, 1:] & ok[:, :-1]
    dx = (o3[c][:, 1:] - o3[c][:, :-1])[pair]
    sig = 1.4826 * np.median(np.abs(dx[::max(1, dx.size // 4000000)])) / np.sqrt(2)
    print(f"channel {c}: the broad part taken, in pixel sigma: median {np.median(broad[ok]) / sig:+.2f}, 10th to 90th percentile {np.percentile(broad[ok], 10) / sig:+.2f} to {np.percentile(broad[ok], 90) / sig:+.2f}")
fits.PrimaryHDU(out[0] if ours.ndim == 2 else out, header).writeto(OUT, overwrite=True)
print(OUT)
