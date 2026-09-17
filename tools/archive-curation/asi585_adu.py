"""Does a real ASI585 bias settle the ADU-scale question that parked three sessions?

filter-inference.md parks the ASI585MC Pro because its green Bayer sites have a hard minimum gap of
exactly 16 between distinct values while red and blue are adjacent-integer, and it could not tell
whether that is a genuine 16x gain difference or a coarser rounding of the same scale:

    "not yet known, because no bias or dark exists anywhere in the archive for this rig to check
     the absolute floor per channel. Until that is shot, do not assume either direction."

`2025/2025-03-20` holds 200 BIAS frames at g252 o13 on that body. The absolute floor is the test: if
green's bias level sits at ~16x red's and blue's, the scale really differs per channel; if all three
sit at the same level and only green's STEP is 16, it is rounding and the ratios need no correction.
"""
import glob
import numpy as np
from astropy.io import fits

BIAS = "D:/Astro-Pics/2025/2025-03-20"


def raw(p):
    with fits.open(p, memmap=False, do_not_scale_image_data=True) as hd:
        h = hd[0].header
        a = hd[0].data.astype(np.float64)
        a = a + float(h.get("BZERO", 0.0)) * float(h.get("BSCALE", 1.0))
    return a, h


def planes(a, pat):
    o = {}
    for i, ch in enumerate(pat):
        dy, dx = divmod(i, 2)
        o.setdefault(ch, []).append(a[dy::2, dx::2])
    return {k: np.concatenate([x.ravel() for x in v]) for k, v in o.items()}


fs = []
for p in sorted(glob.glob(BIAS + "/**/*.fit*", recursive=True)):
    try:
        with fits.open(p, memmap=False) as h:
            hdr = h[0].header
    except Exception:
        continue
    if str(hdr.get("IMAGETYP", "")).upper().startswith("BIAS"):
        fs.append(p)
    if len(fs) >= 12:
        break
print(f"{len(fs)} bias frames read")

a, h = raw(fs[0])
pat = str(h.get("BAYERPAT", "RGGB"))
print(f"BAYERPAT {pat!r}  INSTRUME {h.get('INSTRUME')}  gain {h.get('GAIN')} offset {h.get('OFFSET')}"
      f"  SWCREATE {h.get('SWCREATE', '?')}\n")

stack = np.median(np.stack([raw(p)[0] for p in fs]), axis=0)
pl = planes(stack, pat)

print("per-channel BIAS level (the absolute floor the parked question needs):")
for k in ("R", "G", "B"):
    v = pl[k]
    print(f"  {k}: median {np.median(v):9.2f}   mean {v.mean():9.2f}   p1 {np.percentile(v,1):8.2f}"
          f"   p99 {np.percentile(v,99):8.2f}")

print("\nquantisation STEP per channel, on a single frame (min gap between distinct values):")
one, _ = raw(fs[0])
p1 = planes(one, pat)
for k in ("R", "G", "B"):
    u = np.unique(p1[k][:200000])
    d = np.diff(u)
    d = d[d > 0]
    print(f"  {k}: {len(u):6d} distinct values, min gap {d.min() if len(d) else float('nan'):.0f}")

rg = np.median(pl["G"]) / np.median(pl["R"])
bg = np.median(pl["B"]) / np.median(pl["G"])
print(f"\n  G/R bias-level ratio {rg:.4f}   B/G {bg:.4f}")
print("  a genuine 16x per-channel gain difference would put G/R near 16; near 1 means the step is"
      " rounding and the SCALE is shared.")
