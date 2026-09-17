"""Phase-correlate the RAW near6 subs against the raw reference sub on one green photosite plane
(half resolution; shifts are doubled back to sensor pixels). This is the true frame-to-frame drift,
independent of TianWen's registration; compare with the manifest translation.
"""
import glob
import json
import os

import numpy as np
from astropy.io import fits

manifest = json.load(open("C:/temp/e2/e210-orion/master_GreatOrionNebula_light_120s_12C_g120-near6.manifest.json"))
frames = [(f["Path"], f["StarTransform"]) for f in manifest["Frames"]]
ref_path = [p for p, _ in frames if "0033" in os.path.basename(p)][0]

def green_plane(path):
    with fits.open(path, memmap=False) as h:
        d = np.asarray(h[0].data, dtype=np.float64)
    # RGGB: green at (0,1) and (1,0); take the (0,1) photosite plane.
    return d[0::2, 1::2]

def highpass(a, k=7):
    p = np.pad(a, k, mode="reflect")
    c = np.cumsum(np.cumsum(p, axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    s = c[2 * k + 1:, 2 * k + 1:] - c[:-2 * k - 1, 2 * k + 1:] - c[2 * k + 1:, :-2 * k - 1] + c[:-2 * k - 1, :-2 * k - 1]
    return a - s / float((2 * k + 1) ** 2)

def peak_shift(a, b):
    fa = np.fft.fft2(a); fb = np.fft.fft2(b)
    r = fa * np.conj(fb); r /= np.maximum(np.abs(r), 1e-12)
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
    return -dx, -dy

ref = green_plane(ref_path)
H, W = ref.shape
size = 384
origins = [(y, x) for y in (150, H // 2 - size // 2, H - 150 - size) for x in (150, W // 2 - size // 2, W - 150 - size)]
print(f"green photosite plane {W}x{H} (half res); reference {os.path.basename(ref_path)}")
print(f"{'frame':<44} {'manifest t':>16} {'raw drift d (sensor px)':>26} {'-2t':>16}   per-patch d (sensor px)")
for path, t in frames:
    img = green_plane(path)
    ds = []
    for (y0, x0) in origins:
        a = highpass(ref[y0:y0 + size, x0:x0 + size]); b = highpass(img[y0:y0 + size, x0:x0 + size])
        a = a - a.mean(); b = b - b.mean()
        wy = np.hanning(size)[:, None]; wx = np.hanning(size)[None, :]
        dx, dy = peak_shift(a * wy * wx, b * wy * wx)
        ds.append((2 * dx, 2 * dy))
    ds = np.array(ds)
    med = np.median(ds, axis=0)
    per = " ".join(f"({x:5.2f},{y:5.2f})" for x, y in ds)
    print(f"{os.path.basename(path)[:44]:<44} ({t[4]:6.2f},{t[5]:6.2f})   ({med[0]:6.2f},{med[1]:6.2f})            ({-2*t[4]:6.2f},{-2*t[5]:6.2f})   {per}")
