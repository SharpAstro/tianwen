"""Where the truth plate's small-scale structure sits in a crowded cell (2026-11-18).
    python -I mottle_split.py <session stem>
Structure = the plate after a 3 px box less its own 21 px median, in the cell's noise sigma. Pixels are classed by the
plate's own records: inpainted (the mask), at a subtracted star (within 3 px, not inpainted), at a candidate left in
(any other outcome, within 3 px), and untouched. Per class: share of the cell, rms and mean of the structure, share of
the cell's structure variance over what a sparse cell holds."""
import os, csv, gzip, sys, collections
import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter, median_filter, binary_dilation
STORE = os.environ.get("TIANWEN_SR_STORE", "D:/Astro-Dataset/2026-10-12-full"); B = 300; stem = sys.argv[1]
P = f"{STORE}/starless/plates/{stem}_plate"
plate = np.asarray(fits.getdata(P + ".fits"), np.float32)
inp = np.asarray(fits.getdata(P + ".inpainted.fits.gz")) > 0
if inp.ndim == 3: inp = inp.any(0)
stars = list(csv.DictReader(open(P + ".stars.csv", encoding="utf-8")))
print(stem[:60], "outcomes:", dict(collections.Counter(s["outcome"] for s in stars)))
H, W = plate.shape[1:]
sub = np.zeros((H, W), bool); knot = np.zeros((H, W), bool)
for s in stars:
    x, y = int(round(float(s["x"]))), int(round(float(s["y"])))
    if 0 <= x < W and 0 <= y < H: (sub if s["outcome"] == "Subtracted" else knot)[y, x] = True
disc = np.hypot(*np.mgrid[-3:4, -3:4]) <= 3
sub = binary_dilation(sub, disc); knot = binary_dilation(knot, disc)
x = np.array([float(s["x"]) for s in stars]); y = np.array([float(s["y"]) for s in stars])
h, _, _ = np.histogram2d(y, x, bins=[H // B, W // B], range=[[0, H // B * B], [0, W // B * B]])
order = np.argsort(-h, axis=None)
cells = [np.unravel_index(k, h.shape) for k in order[:5]] + [np.unravel_index(np.argsort(h, axis=None)[h.size // 4], h.shape)]
lum = plate.mean(0)
for n, (i, j) in enumerate(cells):
    sl = (slice(i * B, (i + 1) * B), slice(j * B, (j + 1) * B)); p = lum[sl]
    if not np.isfinite(p).all(): continue
    sig = 1.4826 * np.median(np.abs(np.diff(p, axis=1))) / np.sqrt(2) / 3.0   # of a 3 px box mean, white noise
    st = (uniform_filter(p, 3) - median_filter(p, 21)) / sig
    cls = {"inpainted": inp[sl], "subtracted site": sub[sl] & ~inp[sl], "candidate left in": knot[sl] & ~inp[sl] & ~sub[sl]}
    cls["untouched"] = ~(cls["inpainted"] | cls["subtracted site"] | cls["candidate left in"])
    tot = (st ** 2).sum()
    print(f"cell {i},{j}: {int(h[i, j])} catalogued, structure rms {np.sqrt((st ** 2).mean()):.2f} (white noise reads about 1)" + ("   [sparse cell, for scale]" if n == len(cells) - 1 else ""))
    for k, m in cls.items():
        if m.sum(): print(f"    {k:18s} {100 * m.mean():5.1f} % of pixels   rms {np.sqrt((st[m] ** 2).mean()):5.2f}   mean {st[m].mean():+5.2f}   {100 * (st[m] ** 2).sum() / tot:5.1f} % of the structure   over +3: {100 * (st[m] > 3).mean():4.1f} %  under -3: {100 * (st[m] < -3).mean():4.1f} %")
