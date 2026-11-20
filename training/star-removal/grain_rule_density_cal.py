"""Calibrate grain_rule_post.py's density stand-in against the plate builder's own subtracted stars (2026-11-20).
    python -I grain_rule_density_cal.py <store> <session stem> [...]
Per session: the share of the frame the builder's subtracted-star density puts over 0.003 and over 0.008 a pixel (61 px
box), and the same for the proxy (the input's local maxima over 5 px standing k LOCAL sigma over its 15 px median) at
several k, with the count of stars each way and the agreement of the two crowded masks (over 0.0055, the weight's half)."""
import csv, sys
from pathlib import Path

import numpy as np
from astropy.io import fits
from scipy.ndimage import maximum_filter, median_filter, uniform_filter

sys.path.insert(0, str(Path(__file__).resolve().parent))   # python -I leaves the script's folder off the path
import plate_grain  # noqa: E402

store = sys.argv[1]
for stem in sys.argv[2:]:
    m = np.asarray(fits.getdata(f"{store}/session-masters/{stem}.fits"), np.float32)
    m3 = m[None] if m.ndim == 2 else m[:3]
    H, W = m3.shape[1:]
    d0 = np.zeros((H, W), np.float32); n0 = 0
    for s in csv.DictReader(open(f"{store}/starless/plates/{stem}_plate.stars.csv", encoding="utf-8")):
        if s["outcome"] == "Subtracted":
            x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
            if 0 <= x < W and 0 <= y < H: d0[y, x] += 1; n0 += 1
    d0 = uniform_filter(d0, 61)
    print(f"{stem[:60]}: builder {n0} subtracted stars; over 0.003 on {100 * (d0 > 0.003).mean():.1f} %, over 0.008 on {100 * (d0 >= 0.008).mean():.1f} %")
    lum = np.nanmean(m3, axis=0)
    ok = np.isfinite(lum) & (lum != 0)
    L = np.where(ok, lum, np.nanmedian(lum[ok]))
    sig = plate_grain.noise_map(L)
    top = (L == maximum_filter(L, 5)) & ok
    over = (L - median_filter(L, 15)) / np.maximum(sig, 1e-12)
    for k in (5, 8, 12, 20, 30):
        p = top & (over > k)
        d = uniform_filter(p.astype(np.float32), 61)
        a, b = d0 > 0.0055, d > 0.0055
        print(f"   k {k:2d}: {int(p.sum()):7d} peaks; over 0.003 on {100 * (d > 0.003).mean():5.1f} %, over 0.008 on {100 * (d >= 0.008).mean():5.1f} %; crowded masks agree on {100 * (a == b).mean():5.1f} % of the frame")
