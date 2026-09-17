"""Look at one edge of a master: the line-median profile, and the band itself.

For the mild cases, where the band is a few times the interior rather than thousands, marking
individual pixels shows nothing. What shows is the profile, so this prints it and renders the band
with every line above the bar tinted, which keeps the picture visible underneath.
"""
import sys
import numpy as np
from astropy.io import fits
from PIL import Image as PILImage

PINK = np.array([255, 0, 170], dtype=np.float32)
BAR = 1.5          # tint a line at this multiple of the interior median
DEPTH = 96         # how deep to look
STRIP = 160        # how deep to render


def load(path):
    with fits.open(path, memmap=False) as hdul:
        hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
        cube = np.asarray(hdu.data, dtype=np.float32)
    if cube.ndim == 2:
        cube = cube[None, ...]
    if cube.ndim == 3 and cube.shape[-1] in (3, 4) and cube.shape[0] > 4:
        cube = np.moveaxis(cube, -1, 0)
    return cube


def stretch(cube, lo, hi, m=0.07):
    chans = []
    for ch in range(min(3, cube.shape[0])):
        n = np.clip((np.nan_to_num(cube[ch], nan=lo) - lo) / max(hi - lo, 1e-9), 0, 1)
        chans.append(np.clip(((m - 1) * n) / ((2 * m - 1) * n - m), 0, 1))
    g = chans[0] if len(chans) == 1 else np.clip(sum(chans) / len(chans), 0, 1)
    return (np.repeat(g[..., None], 3, axis=-1) * 255).astype(np.uint8)


path, edge, out = sys.argv[1], sys.argv[2], sys.argv[3]
cube = load(path)
c, h, w = cube.shape
p = cube[0]
core = p[h // 4: 3 * h // 4, w // 4: 3 * w // 4]
core = core[np.isfinite(core) & (core != 0)]
med = float(np.median(core))
allv = p[np.isfinite(p) & (p != 0)]
lo, hi = float(np.percentile(allv, 0.5)), float(np.percentile(allv, 99.5))

def line(i):
    if edge == "top":
        return p[i], (i, slice(None))
    if edge == "bottom":
        return p[h - 1 - i], (h - 1 - i, slice(None))
    if edge == "left":
        return p[:, i], (slice(None), i)
    return p[:, w - 1 - i], (slice(None), w - 1 - i)

print(f"    {c} x {h} x {w}, interior median {med:.6g}, edge = {edge}")
print("    line   median    x interior   px used")
flagged = []
for i in range(DEPTH):
    v, idx = line(i)
    vv = v[np.isfinite(v) & (v != 0)]
    if vv.size < 0.5 * v.size:
        print(f"    {i:4d}   (mostly absent, {vv.size}/{v.size} px)")
        continue
    r = float(np.median(vv)) / med
    if r >= BAR:
        flagged.append(i)
    if i < 24 or r >= BAR:
        print(f"    {i:4d}  {np.median(vv):9.5g}  {r:9.3f}x   {vv.size}")
print(f"    lines at or over {BAR}x: {flagged if flagged else 'none'}")

rgb = stretch(cube, lo, hi).astype(np.float32)
for i in flagged:
    _, idx = line(i)
    rgb[idx] = rgb[idx] * 0.45 + PINK * 0.55
rgb = rgb.astype(np.uint8)

if edge in ("top", "bottom"):
    band = rgb[:STRIP] if edge == "top" else rgb[h - STRIP:]
    img = PILImage.fromarray(band).resize((1400, int(STRIP * 1400 / w * 4)), PILImage.NEAREST)
else:
    band = rgb[:, :STRIP] if edge == "left" else rgb[:, w - STRIP:]
    img = PILImage.fromarray(band).resize((STRIP * 4, 900), PILImage.NEAREST)
img.save(f"{out}-band.png")

f = max(1, int(round(w / 1200)))
PILImage.fromarray(rgb).resize((w // f, h // f), PILImage.LANCZOS).save(f"{out}-full.png")
print(f"    wrote {out}-full.png and {out}-band.png")
