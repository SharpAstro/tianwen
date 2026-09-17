"""Run NeedsStretch's statistic over every master of a bake and name what it refuses.

Channel 0 only, which is what the gate reads, and over the unit-range division the exporter applies
first. Prints every master at or over the threshold, plus the same statistic inside a central crop,
which is what the "crop before the gate" fix would do.
"""
import glob
import os
import sys
import numpy as np
from astropy.io import fits

THRESHOLD = 0.125
root = sys.argv[1]
rows = []
files = sorted(glob.glob(os.path.join(root, "*.fits")))
print(f"{len(files)} masters under {root}", flush=True)

for i, path in enumerate(files):
    try:
        with fits.open(path, memmap=True) as hdul:
            hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
            shape = hdu.shape
            if len(shape) == 3 and shape[0] <= 4:
                plane = np.asarray(hdu.data[0], dtype=np.float32)
                mx = max(float(np.nanmax(np.asarray(hdu.data[c], dtype=np.float32)))
                         for c in range(shape[0]))
            else:
                plane = np.asarray(hdu.data, dtype=np.float32)
                mx = float(np.nanmax(plane))
    except Exception as e:  # a master that will not open is its own finding
        print(f"  !! {os.path.basename(path)[:60]}: {e}", flush=True)
        continue

    p = plane / mx if mx else plane
    finite = p[np.isfinite(p)]
    if finite.size == 0:
        continue
    lo = float(finite.min())
    s = float(np.median(finite - lo))

    h, w = p.shape
    dy, dx = int(h * 0.05), int(w * 0.05)
    c = p[dy:h - dy, dx:w - dx]
    cf = c[np.isfinite(c)]
    lo2 = float(cf.min())
    s2 = float(np.median(cf - lo2))

    rows.append((s, s2, lo, lo2, os.path.basename(path)))
    if (i + 1) % 20 == 0:
        print(f"  ... {i+1}/{len(files)}", flush=True)

rows.sort(reverse=True)
print("\n=== top 10 by the gate statistic (>= %.3f is REFUSED) ===" % THRESHOLD)
print(f"{'whole':>8} {'crop95':>8} {'min':>11} {'min(crop)':>11}  master")
for s, s2, lo, lo2, name in rows[:10]:
    flag = "  <-- REFUSED" if s >= THRESHOLD else ""
    print(f"{s:8.4f} {s2:8.4f} {lo:11.6g} {lo2:11.6g}  {name[:62]}{flag}")

refused = [r for r in rows if r[0] >= THRESHOLD]
print(f"\nrefused: {len(refused)} of {len(rows)}")
for s, s2, lo, lo2, name in refused:
    print(f"  {name}")
    print(f"    whole {s:.4f} (min {lo:.6g}) -> crop95 {s2:.4f} (min {lo2:.6g}): "
          f"{'ADMITTED' if s2 < THRESHOLD else 'still refused'}")
