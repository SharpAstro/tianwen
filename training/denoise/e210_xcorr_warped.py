"""Phase-correlate each warped (normalised) near6 frame against the reference frame on the shared
canvas. Independent of star centroids: a whole-frame shift shows up as the correlation peak's
position. Reports the peak shift per patch (parabolic sub-pixel refinement) for several patches.
"""
import glob
import os
import sys

import numpy as np
from astropy.io import fits

norm_dir = glob.glob(r"C:/temp/e2/e210-orion/exp-near6-norm/_staging/*/normalized")[0]
files = sorted(glob.glob(os.path.join(norm_dir, "*.fits")))
ref_name = "0033"

def green(path):
    with fits.open(path, memmap=False) as h:
        d = h[0].data
    if d.ndim == 3:
        d = d[min(1, d.shape[0] - 1)]
    return np.asarray(d, dtype=np.float64)

def highpass(a):
    # Remove the smooth field: subtract a heavily box-blurred copy (separable, via cumsum).
    k = 15
    p = np.pad(a, k, mode="reflect")
    c = np.cumsum(np.cumsum(p, axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    s = c[2 * k + 1:, 2 * k + 1:] - c[:-2 * k - 1, 2 * k + 1:] - c[2 * k + 1:, :-2 * k - 1] + c[:-2 * k - 1, :-2 * k - 1]
    blur = s / float((2 * k + 1) ** 2)
    return a - blur

def peak_shift(a, b):
    """Shift (dx, dy) that moves b onto a, from the phase correlation peak with a parabolic refine."""
    fa = np.fft.fft2(a)
    fb = np.fft.fft2(b)
    r = fa * np.conj(fb)
    r /= np.maximum(np.abs(r), 1e-12)
    c = np.fft.ifft2(r).real
    iy, ix = np.unravel_index(np.argmax(c), c.shape)
    h, w = c.shape
    def sub(v_m, v_0, v_p):
        den = v_m - 2 * v_0 + v_p
        return 0.0 if den == 0 else 0.5 * (v_m - v_p) / den
    dx = ix + sub(c[iy, (ix - 1) % w], c[iy, ix], c[iy, (ix + 1) % w])
    dy = iy + sub(c[(iy - 1) % h, ix], c[iy, ix], c[(iy + 1) % h, ix])
    if dx > w / 2: dx -= w
    if dy > h / 2: dy -= h
    return dx, dy, c[iy, ix]

ref_path = [f for f in files if ref_name in os.path.basename(f)][0]
ref = green(ref_path)
H, W = ref.shape
print(f"canvas {W}x{H}; reference {os.path.basename(ref_path)}")
size = 512
# Patches spread over the frame but off the canvas rim (which is NaN on some frames).
origins = [(y, x) for y in (300, H // 2 - size // 2, H - 300 - size) for x in (300, W // 2 - size // 2, W - 300 - size)]

print(f"{'frame':<48} " + " ".join(f"{'p' + str(i):>12}" for i in range(len(origins))) + "   median dx, dy")
for f in files:
    img = green(f)
    row = []
    dxs, dys = [], []
    for (y0, x0) in origins:
        a = ref[y0:y0 + size, x0:x0 + size]
        b = img[y0:y0 + size, x0:x0 + size]
        if np.isnan(a).any() or np.isnan(b).any():
            row.append(f"{'nan':>12}")
            continue
        a = highpass(a); b = highpass(b)
        a -= a.mean(); b -= b.mean()
        # Cosine taper against edge leakage.
        wy = np.hanning(size)[:, None]; wx = np.hanning(size)[None, :]
        a *= wy * wx; b *= wy * wx
        dx, dy, pk = peak_shift(a, b)
        # The reference is A and the frame is B: peak at +s means B is displaced by -s from A... define
        # displacement of the frame relative to the reference as (frame star pos - ref star pos).
        dxs.append(-dx); dys.append(-dy)
        row.append(f"{-dx:5.2f},{-dy:5.2f}")
    med = f"{np.median(dxs):6.2f}, {np.median(dys):6.2f}" if dxs else "n/a"
    print(f"{os.path.basename(f)[:48]:<48} " + " ".join(f"{r:>12}" for r in row) + f"   {med}")
