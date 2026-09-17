"""Measure the parked ASI585 sessions' filter, now that the per-channel bias is known.

The IMX533-derived bands do NOT apply to an IMX585, so this does not try to name a filter from them.
What it does is the valid same-sensor comparison: every ASI585 session against the ASI585 reference
frame `frame_00002_LeEnhance.fits`, which the archive already names L-eNhance, using the measured
per-channel bias for that session's own offset.

bias (median ADU, this body, measured 2026-09-16 over 10 frames per set):
    offset  3 : R 285.0  G 224.0  B 285.0
    offset  7 : R 619.5  G 472.0  B 619.5
    offset 13 : R 1120.0 G 864.0  B 1120.0
Red and blue are identical at every offset and green sits at 0.77 of them, so subtracting per
channel is what makes a cross-channel ratio mean anything on this sensor.
"""
import glob
import os
import numpy as np
from astropy.io import fits

BIAS = {3: dict(R=285.0, G=224.0, B=285.0),
        7: dict(R=619.5, G=472.0, B=619.5),
        13: dict(R=1120.0, G=864.0, B=1120.0)}

REF = "D:/Astro-Organized/_provenance/reference-frames/frame_00002_LeEnhance.fits"
SESSIONS = [
    "D:/Astro-Unsorted/SMC 120s LEnh ASI585 252g",
    "D:/Astro-Unsorted/Eta Car 24mm LeHance 60s -10d",
    "D:/Astro-Unsorted/Tarantula Neb  ZS61 ASI585 0x8 120s 7o 252g -10deg 66rb",
    "D:/Astro-Unsorted/SMC ZS61 ASI585 0x8 120s 7o 252g -10deg 66rb",
    "D:/Astro-Unsorted/Vela SNR 60s 6deg",
    "D:/Astro-Pics/2025/2025-03-20",
]


def raw(p):
    with fits.open(p, memmap=False, do_not_scale_image_data=True) as hd:
        h = hd[0].header
        a = hd[0].data.astype(np.float64)
        a = a + float(h.get("BZERO", 0.0)) * float(h.get("BSCALE", 1.0))
    return a, h


def ratios(p):
    a, h = raw(p)
    off = int(h.get("OFFSET", -1) or -1)
    if off not in BIAS:
        return None
    pat = str(h.get("BAYERPAT", "RGGB"))
    o = {}
    for i, ch in enumerate(pat):
        dy, dx = divmod(i, 2)
        o.setdefault(ch, []).append(a[dy::2, dx::2])
    pl = {k: np.concatenate([x.ravel() for x in v]) for k, v in o.items()}
    sky = {k: np.percentile(v, 10) - BIAS[off][k] for k, v in pl.items()}
    if sky["G"] <= 0:
        return None
    return sky["R"] / sky["G"], sky["B"] / sky["G"], off, float(h.get("EXPTIME", 0) or 0), \
        str(h.get("OBJECT", "?"))[:20]


r = ratios(REF)
print(f"REFERENCE frame_00002_LeEnhance (archive says L-eNhance)")
print(f"  offset {r[2]}  exp {r[3]:g}s  R/G {r[0]:.4f}  B/G {r[1]:.4f}\n")

print(f"{'session':44s} {'n':>4s} {'off':>4s}  {'R/G':>7s} {'B/G':>7s}  {'vs ref B/G':>11s}")
for d in SESSIONS:
    lights = []
    for p in sorted(glob.glob(d + "/**/*.fit*", recursive=True)):
        try:
            with fits.open(p, memmap=False) as h:
                hdr = h[0].header
        except Exception:
            continue
        if "ASI585" not in str(hdr.get("INSTRUME", "")):
            continue
        if str(hdr.get("IMAGETYP", "")).upper() not in ("LIGHT", ""):
            continue
        e = float(hdr.get("EXPTIME", 0) or 0)
        if e < 10 or e > 300:          # skip calibration and stacked outputs
            continue
        lights.append(p)
    if not lights:
        print(f"{os.path.basename(d)[:44]:44s}  no usable lights")
        continue
    mid = lights[len(lights) // 2 - 1: len(lights) // 2 + 2]
    vals = [ratios(p) for p in mid]
    vals = [v for v in vals if v]
    if not vals:
        print(f"{os.path.basename(d)[:44]:44s}  offset not in the measured bias table")
        continue
    rg = np.mean([v[0] for v in vals])
    bg = np.mean([v[1] for v in vals])
    print(f"{os.path.basename(d)[:44]:44s} {len(lights):4d} {vals[0][2]:4d}  "
          f"{rg:7.4f} {bg:7.4f}  {bg / r[1]:10.3f}x")
