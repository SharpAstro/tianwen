"""Is the SV545 rig on the same filter as the SW8Q session already filed as IDAS-LPS-D3?

This comparison is legitimate where the cross-sensor one was not: both sessions are the same
QHY294PROC in the same 11M readout mode, so the CFA samples the passband identically and the ratios
are directly comparable. The marker file says IDAS LPS; this is what turns that into evidence.

Also validates the calibration the way the skill requires: median(dark - bias), the fraction of
light - dark below zero, and the headroom at p0.01.
"""
import glob
import numpy as np
from astropy.io import fits

SV = "D:/Astro-Pics/2026/2026-08 SV545"
SW = "D:/Astro-Organized"


def load(p):
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


def pick(paths, want_type=None, want_exp=None, n=3):
    out = []
    for p in paths:
        try:
            with fits.open(p, memmap=False) as h:
                hdr = h[0].header
        except Exception:
            continue
        if want_type and str(hdr.get("IMAGETYP", "")).upper() != want_type:
            continue
        if want_exp is not None and abs(float(hdr.get("EXPTIME", 0) or 0) - want_exp) > 0.05:
            continue
        out.append(p)
        if len(out) >= n:
            break
    return out


allf = sorted(glob.glob(SV + "/**/*.fit*", recursive=True))
bias = pick(allf, "BIAS", 0.0, 5)
dark = pick(allf, "DARK", 60.0, 5)
dflat = pick(allf, "DARK", 10.10, 5)
flat = pick(allf, "FLAT", 10.10, 5)
print(f"picked: bias {len(bias)}, dark60 {len(dark)}, darkflat10.1 {len(dflat)}, flat {len(flat)}")


def stack_med(paths):
    a = []
    for p in paths:
        d, h = load(p)
        a.append(d)
    return np.median(np.stack(a), axis=0), h


bm, _ = stack_med(bias)
dm, _ = stack_med(dark)
dfm, _ = stack_med(dflat)
fm, fh = stack_med(flat)
pat = str(fh.get("BAYERPAT", "RGGB"))
print(f"BAYERPAT {pat!r}\n")

print("calibration validity (one-sided: rules out OVER-subtraction only)")
print(f"  median(dark - bias)  = {float(np.median(dm - bm)):+.3f}")
fr = fm - dfm
print(f"  flat - darkflat      : {float((fr < 0).mean())*100:.3f}% negative, p0.01 {np.percentile(fr, 0.01):+.1f}")

lights = [p for p in allf]
byobj = {}
for p in lights:
    try:
        with fits.open(p, memmap=False) as h:
            hdr = h[0].header
    except Exception:
        continue
    if str(hdr.get("IMAGETYP", "")).upper() != "LIGHT":
        continue
    if abs(float(hdr.get("EXPTIME", 0) or 0) - 60.0) > 0.5:
        continue          # the 5820/7920/13260 s entries are stacked outputs
    byobj.setdefault(str(hdr.get("OBJECT", "?")), []).append(p)

print(f"\nflat channel ratios  R/G {planes(fr, pat)['R'].mean()/planes(fr, pat)['G'].mean():.4f}"
      f"   B/G {planes(fr, pat)['B'].mean()/planes(fr, pat)['G'].mean():.4f}")

print(f"\n{'target':26s} {'n':>4s}  {'sky R/G':>8s} {'sky B/G':>8s}  {'l-d <0':>8s} {'p0.01':>8s}")
for obj, ps in sorted(byobj.items()):
    mid = ps[len(ps) // 2 - 1: len(ps) // 2 + 2]
    rg, bg, neg, p01 = [], [], [], []
    for p in mid:
        d, _ = load(p)
        res = d - dm
        neg.append(float((res < 0).mean()) * 100)
        p01.append(np.percentile(res, 0.01))
        pl = planes(d, pat)
        sky = {k: np.percentile(v, 10) - np.percentile(planes(bm, pat)[k], 10) for k, v in pl.items()}
        rg.append(sky["R"] / sky["G"])
        bg.append(sky["B"] / sky["G"])
    print(f"{obj:26s} {len(ps):4d}  {np.mean(rg):8.4f} {np.mean(bg):8.4f}  "
          f"{np.mean(neg):7.3f}% {np.mean(p01):8.1f}")

print("\nfor comparison, SW8Q (same QHY294PROC, filed IDAS-LPS-D3): sky B/G 0.556 / 0.604 / 0.606")
