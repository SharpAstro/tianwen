"""Reproduce NeedsStretch's statistic on a master, whole frame against a crop.

The gate is ChunkedNafnetRunner.NeedsStretch: channel 0, min over the finite pixels, then
median(v - min) against StretchAutoDetectMedianThreshold (0.125). Under the bar means "stretch me";
at or over it means "already stretched, feed verbatim", which for a linear master is the defect.
"""
import sys
import numpy as np
from astropy.io import fits


def stat(plane):
    finite = plane[np.isfinite(plane)]
    if finite.size == 0:
        return float("nan"), float("nan")
    lo = float(finite.min())
    return lo, float(np.median(finite - lo))


path, label = sys.argv[1], sys.argv[2]
with fits.open(path, memmap=False) as hdul:
    for hdu in hdul:
        if hdu.data is not None and hdu.data.ndim >= 2:
            cube = np.asarray(hdu.data, dtype=np.float32)
            break
if cube.ndim == 2:
    cube = cube[None, ...]
if cube.ndim == 3 and cube.shape[-1] in (3, 4) and cube.shape[0] > 4:
    cube = np.moveaxis(cube, -1, 0)

# The exporter divides by the observed maximum first (ToUnitRange); the statistic is scale
# sensitive, so do the same.
mx = float(np.nanmax(cube))
p0 = cube[0] / mx

lo, s = stat(p0)
zeros = int((cube == 0).all(axis=0).sum())
nan_border = int(np.isnan(cube[0]).sum())
print(f"=== {label}")
print(f"    max used for unit range : {mx:.6g}")
print(f"    whole frame: min {lo:.6g}  median(v - min) {s:.4f}  -> "
      f"{'REFUSED (reads as stretched)' if s >= 0.125 else 'stretched normally'}")
print(f"    exact-zero px {zeros:,}   channel-0 NaN {nan_border:,}")

# Where does that minimum live? If it is the canvas edge the crop removes it.
ys, xs = np.where(np.isclose(p0, lo, rtol=0, atol=1e-7))
if ys.size:
    h, w = p0.shape
    edge = ((ys < 0.02 * h) | (ys > 0.98 * h) | (xs < 0.02 * w) | (xs > 0.98 * w)).mean()
    print(f"    minimum occurs {ys.size:,} times, {edge*100:.1f}% within 2% of a border")

# The same statistic inside a central crop, standing in for the covered rectangle.
for keep in (0.95, 0.90, 0.80):
    h, w = p0.shape
    dy, dx = int(h * (1 - keep) / 2), int(w * (1 - keep) / 2)
    lo2, s2 = stat(p0[dy:h - dy, dx:w - dx])
    print(f"    central {keep:.0%}: min {lo2:.6g}  median(v - min) {s2:.4f}  -> "
          f"{'still refused' if s2 >= 0.125 else 'ADMITTED'}")
