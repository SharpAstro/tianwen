"""Render a master and mark the pixels that run orders of magnitude above its sky.

Same idea as hole_map but for the opposite defect: instead of absence, runaway values. The picture
is stretched off ordinary percentiles so the nebula is visible at all, and anything over the
threshold is painted pink, dilated so a single pixel survives the downsample.
"""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image as PILImage
from scipy import ndimage

PINK = np.array([255, 0, 170], dtype=np.uint8)


def load(path):
    with fits.open(path, memmap=False) as hdul:
        hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
        cube = np.asarray(hdu.data, dtype=np.float32)
        hdr = dict(hdu.header)
    if cube.ndim == 2:
        cube = cube[None, ...]
    if cube.ndim == 3 and cube.shape[-1] in (3, 4) and cube.shape[0] > 4:
        cube = np.moveaxis(cube, -1, 0)
    return cube, hdr


def block_any(mask, f):
    h, w = mask.shape
    h2, w2 = (h // f) * f, (w // f) * f
    return mask[:h2, :w2].reshape(h2 // f, f, w2 // f, f).any(axis=(1, 3))


path, out = sys.argv[1], sys.argv[2]
cube, hdr = load(path)
c, h, w = cube.shape
plane = cube[0]
finite = plane[np.isfinite(plane)]
med = float(np.median(finite))
p999 = float(np.percentile(finite, 99.9))

# "Runaway" is relative to the frame's own bright end, not an absolute: a real star tops out near
# saturation, so anything a hundred times past the 99.9th percentile is not a star.
thresh = p999 * 100.0
hot = np.isfinite(cube).any(axis=0) & (np.nanmax(cube, axis=0) > thresh)
lbl, n = ndimage.label(hot, structure=np.ones((3, 3)))
sizes = ndimage.sum(hot, lbl, range(1, n + 1)) if n else np.array([])

print(f"    shape {c} x {h} x {w}   sky median {med:.4g}   p99.9 {p999:.4g}")
print(f"    threshold (100x p99.9) = {thresh:.4g}")
print(f"    runaway pixels {int(hot.sum()):,} in {n} components, "
      f"largest {int(sizes.max()) if n else 0} px")
for ch in range(c):
    m = np.isfinite(cube[ch]) & (cube[ch] > thresh)
    print(f"      channel {ch}: {int(m.sum()):,} px, peak {np.nanmax(cube[ch]):.4g}")
if n:
    ys, xs = np.where(hot)
    print(f"    rows {ys.min()}..{ys.max()} of {h}, cols {xs.min()}..{xs.max()} of {w}")
    print(f"    distinct rows {len(np.unique(ys)):,}, distinct cols {len(np.unique(xs)):,}")

# Ordinary display stretch, computed on the sane part of the histogram.
lo = float(np.percentile(finite, 0.5))
hi = float(np.percentile(finite, 99.5))
chans = []
for ch in range(min(3, c)):
    nrm = np.clip((np.nan_to_num(cube[ch], nan=lo) - lo) / max(hi - lo, 1e-9), 0, 1)
    m = 0.06  # fixed midtone: the point is the marks, not a pretty curve
    nrm = ((m - 1) * nrm) / ((2 * m - 1) * nrm - m)
    chans.append(np.clip(nrm, 0, 1))
grey = chans[0] if c == 1 else np.clip(sum(chans) / len(chans), 0, 1)
rgb = (np.repeat(grey[..., None], 3, axis=-1) * 255).astype(np.uint8)

f = max(1, int(round(w / 1400)))
small = np.array(PILImage.fromarray(rgb).resize((w // f, h // f), PILImage.LANCZOS))
hs = ndimage.binary_dilation(block_any(hot, f), np.ones((3, 3), bool))
sh, sw = min(small.shape[0], hs.shape[0]), min(small.shape[1], hs.shape[1])
small[:sh, :sw][hs[:sh, :sw]] = PINK
PILImage.fromarray(small).save(f"{out}-full.png")

if n:
    big = int(np.argmax(sizes)) + 1
    ys, xs = np.where(lbl == big)
    cy, cx = int(ys.mean()), int(xs.mean())
    half = 200
    y0, x0 = max(0, cy - half), max(0, cx - half)
    y1, x1 = min(h, y0 + 2 * half), min(w, x0 + 2 * half)
    crop = rgb[y0:y1, x0:x1].copy()
    crop[hot[y0:y1, x0:x1]] = PINK
    PILImage.fromarray(crop).resize(((x1 - x0) * 2, (y1 - y0) * 2), PILImage.NEAREST).save(
        f"{out}-zoom.png")
    print(f"    zoom centred on the largest component at ({cx}, {cy})")
