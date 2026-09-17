"""How far above the interior does an edge band's LEVEL get, across a whole bake?

CoverageEdgeWalk is a noise rule, and its remarks record that level matching was tried and rejected
because it trimmed 300+ px of a drizzle edge where the truth was 4. That rejection was at the 1.1x
scale. Overscan is not at that scale, so the question is whether the two separate cleanly enough
that a gross level rule can catch one without ever touching the other.

For each master, per edge, the maximum over the outermost 32 lines of (line median / interior
median), counting only lines that are not mostly absent. Absent pixels are excluded so the canvas
ring does not answer for the band.
"""
import glob
import os
import sys
import numpy as np
from astropy.io import fits

DEPTH = 32


def line_ratio(plane, interior_med):
    """(max ratio, where) over the outermost DEPTH lines of all four edges."""
    worst, where = 0.0, ""
    h, w = plane.shape
    for name, lines in (
        ("top", (plane[i] for i in range(DEPTH))),
        ("bottom", (plane[h - 1 - i] for i in range(DEPTH))),
        ("left", (plane[:, i] for i in range(DEPTH))),
        ("right", (plane[:, w - 1 - i] for i in range(DEPTH))),
    ):
        for i, line in enumerate(lines):
            v = line[np.isfinite(line) & (line != 0)]
            if v.size < 0.5 * line.size:      # mostly absent: the ring, not a band
                continue
            r = float(np.median(v)) / interior_med
            if r > worst:
                worst, where = r, f"{name}+{i}"
    return worst, where


root = sys.argv[1]
rows = []
for path in sorted(glob.glob(os.path.join(root, "*.fits"))):
    with fits.open(path, memmap=True) as hdul:
        hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
        plane = np.asarray(hdu.data[0] if (len(hdu.shape) == 3 and hdu.shape[0] <= 4)
                           else hdu.data, dtype=np.float32)
    h, w = plane.shape
    core = plane[h // 4: 3 * h // 4, w // 4: 3 * w // 4]
    core = core[np.isfinite(core) & (core != 0)]
    if core.size == 0:
        continue
    med = float(np.median(core))
    r, where = line_ratio(plane, med)
    rows.append((r, where, os.path.basename(path)))

rows.sort(reverse=True)
print(f"{len(rows)} masters, worst edge-line median as a multiple of the interior median\n")
for r, where, name in rows[:8]:
    print(f"  {r:10.2f}x  {where:10s}  {name[:60]}")
print("  ...")
for r, where, name in rows[-3:]:
    print(f"  {r:10.2f}x  {where:10s}  {name[:60]}")

vals = np.array([r for r, _, _ in rows])
print(f"\n  p50 {np.median(vals):.3f}x   p90 {np.percentile(vals, 90):.3f}x   "
      f"p99 {np.percentile(vals, 99):.3f}x   max {vals.max():.1f}x")
for bar in (2, 3, 5, 10, 50):
    print(f"  a {bar}x bar would trim on {int((vals > bar).sum())} of {len(vals)} masters")
