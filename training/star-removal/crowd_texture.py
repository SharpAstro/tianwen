"""Small-scale structure a starless plate carries in a crowded field, ours against the truth plate (2026-11-18).
    python -I crowd_texture.py <onnx> <session stem>
Per 300 px cell, the densest by catalogued stars: the spread of each image after a 3 px box, in the cell's noise sigma
(neighbour differences of the truth plate), for the truth plate, our output and our output less the truth."""
import csv, os, subprocess, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import uniform_filter
from PIL import Image
STORE = os.environ.get("TIANWEN_SR_STORE", "D:/Astro-Dataset/2026-10-12-full"); OUT = os.environ.get("TIANWEN_SR_OUT", "D:/temp/tianwen-scratch/crowd-look"); CLI = os.environ.get("TIANWEN_SR_CLI", "tianwen"); B = 300
onnx, stem = sys.argv[1], sys.argv[2]
master = np.asarray(fits.getdata(f"{STORE}/session-masters/{stem}.fits"), np.float32)
plate = np.asarray(fits.getdata(f"{STORE}/starless/plates/{stem}_plate.fits"), np.float32)
dst = f"{OUT}/smc-out.fits"
if not os.path.exists(dst):
    r = subprocess.run([CLI, "image", "remove-stars", f"{STORE}/session-masters/{stem}.fits", "-o", dst, "--no-stars", "--views", "1", "--model", onnx],
                       capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
    if r.returncode: raise SystemExit(r.stdout[-1500:] + r.stderr[-1500:])
out = np.asarray(fits.getdata(dst), np.float32)
stars = list(csv.DictReader(open(f"{STORE}/starless/plates/{stem}_plate.stars.csv", encoding="utf-8")))
x = np.array([float(s["x"]) for s in stars]); y = np.array([float(s["y"]) for s in stars])
H, W = master.shape[1:]
h, _, _ = np.histogram2d(y, x, bins=[H // B, W // B], range=[[0, H // B * B], [0, W // B * B]])
cells = [np.unravel_index(k, h.shape) for k in np.argsort(-h, axis=None)[:6]] + [np.unravel_index(np.argsort(h, axis=None)[h.size // 4], h.shape)]
print(stem, master.shape, len(stars), "catalogued")
print("cell   stars   level of plate R/G/B         texture (3 px box) in sigma, R/G/B:  truth plate        ours            ours - truth      mean of ours - truth, sigma")
for n, (i, j) in enumerate(cells):
    sl = (slice(None), slice(i * B, (i + 1) * B), slice(j * B, (j + 1) * B))
    m, p, o = master[sl], plate[sl], out[sl]
    if not (np.isfinite(p).all() and np.isfinite(o).all()): continue
    sig = np.array([1.4826 * np.median(np.abs(np.diff(p[c], axis=1))) / np.sqrt(2) for c in range(3)])
    t = lambda a: "/".join(f"{np.std(uniform_filter(a[c], 3)) / sig[c]:.2f}" for c in range(3))
    print(f"{i},{j:<3d} {int(h[i, j]):6d}   {'/'.join(f'{np.median(c):.5f}' for c in p)}    {t(p):>18s} {t(o):>18s} {t(o - p):>18s}     {'/'.join(f'{np.mean((o - p)[c]) / sig[c]:+.2f}' for c in range(3))}"
          + ("   (a sparse cell, for scale)" if n == len(cells) - 1 else ""))
    if n < 2:
        ref = np.median(p, axis=(1, 2)); rows = []
        for k in (25, 8):
            lo = ref - k / 3 * sig; hi = ref + k * sig
            f = lambda a: (np.clip((a - lo[:, None, None]) / (hi - lo)[:, None, None], 0, 1).transpose(1, 2, 0) * 255).astype(np.uint8)
            rows.append(np.concatenate([np.kron(f(a), np.ones((2, 2, 1), np.uint8)) for a in (m, p, o)], axis=1))
        Image.fromarray(np.concatenate(rows, axis=0)).save(f"{OUT}/smc{n}.png")
