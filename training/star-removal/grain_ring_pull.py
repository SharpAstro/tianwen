"""Does the corrected grain rule pull the sky beside a zero ring toward its flat fill? (PR #2297's review, 2026-11-20.)
    python -I grain_ring_pull.py <stored plates> <corrected plates> <file of stems>
Per plate with a zero ring: the share of valid pixels the rule changed by over 10 % of their value (either way), and the
median relative change, in the band within 20 px of the ring and in the band 40 to 60 px from it. A pull toward the fill
shows as the near band changed more than the far one, and with a sign. Prints the medians over the plates and the worst."""
import os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import distance_transform_edt, label

A, B, STEMS = sys.argv[1:4]
rows = []
for stem in [l.strip() for l in open(STEMS, encoding="utf-8-sig") if l.strip()]:
    a = np.asarray(fits.getdata(os.path.join(A, stem + "_plate.fits")), np.float32); a = a[None] if a.ndim == 2 else a
    b = np.asarray(fits.getdata(os.path.join(B, stem + "_plate.fits")), np.float32); b = b[None] if b.ndim == 2 else b
    zeros = (a == 0).all(axis=0)
    reg, _ = label(zeros)
    edge = np.unique(np.concatenate([reg[0], reg[-1], reg[:, 0], reg[:, -1]]))
    ring = np.isin(reg, edge[edge > 0])
    if not ring.any():
        continue
    d = distance_transform_edt(~ring)
    ok = np.isfinite(a).all(axis=0) & ~ring & (a > 0).all(axis=0)
    out = []
    for lo, hi in ((0, 20), (40, 60)):
        m = ok & (d > lo) & (d <= hi)
        if m.sum() < 1000:
            out += [np.nan, np.nan]
            continue
        rel = ((b - a) / a)[:, m]
        out += [100 * float((np.abs(rel) > 0.1).any(axis=0).mean()), 100 * float(np.median(rel))]
    rows.append((stem, *out))
r = np.array([x[1:] for x in rows], float)
print(f"plates with a zero ring read: {len(rows)}")
if not rows:
    sys.exit("no listed plate has a zero ring; nothing to compare")
print(f"changed by over 10 %, share of the band: within 20 px of the ring median {np.nanmedian(r[:, 0]):.2f} %, 40 to 60 px from it {np.nanmedian(r[:, 2]):.2f} %")
print(f"median relative change: within 20 px {np.nanmedian(r[:, 1]):+.3f} %, 40 to 60 px {np.nanmedian(r[:, 3]):+.3f} %")
worst = sorted(rows, key=lambda x: -(x[1] - x[3]) if np.isfinite(x[1]) and np.isfinite(x[3]) else 1e9)[:5]
for w in worst:
    print(f"   near {w[1]:.2f} % (median change {w[2]:+.3f} %), far {w[3]:.2f} % ({w[4]:+.3f} %)  {w[0][:80]}")
