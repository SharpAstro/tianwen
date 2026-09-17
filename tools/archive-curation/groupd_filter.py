"""Group D filter measurement, with the bias the archive was waiting for.

Same method as sv605-measure.py: per-channel bias measured from this rig's own frames, background
B/G on the raw undebayered mosaic as the discriminator, flat channel ratios as the independent
corroboration. filter-inference.md parked this session because gain 1600 had no bias anywhere in
the archive and the flat's RAW ratio overlaps the 3 nm range uncorrected. SW8_QHY294_BACKFILL is
that bias, so the correction can now be applied.

B/G reference bands in filter-inference.md are IMX533-derived and this is an IMX492, so they are
quoted for orientation, not as a verdict. What this can say without a cross-sensor assumption is
(a) whether the correction moves the flat ratio out of the 3 nm overlap, and (b) whether one
filter ran the whole night.
"""
import glob
import numpy as np
from astropy.io import fits

S = "D:/Astro-Pics/2026/2026-02-20 SW8Q Omega Cen + Cen A + Running Chicken Neb"
B = "D:/Astro-Unsorted/SW8_QHY294_BACKFILL"
BANDS = {"3 nm dual-band": (0.44, 0.72), "L-Quad Enhance": (0.90, 1.20)}


def raw(path):
    with fits.open(path, memmap=False, do_not_scale_image_data=True) as hd:
        h = hd[0].header
        a = hd[0].data.astype(np.float64)
        a = a + float(h.get("BZERO", 0.0)) * float(h.get("BSCALE", 1.0))
    return a, h


def planes(a, pat="RGGB"):
    out = {}
    for i, ch in enumerate(pat):
        dy, dx = divmod(i, 2)
        out.setdefault(ch, []).append(a[dy::2, dx::2])
    return {k: np.concatenate([x.ravel() for x in v]) for k, v in out.items()}


def stack_planes(paths, n, stat=np.median):
    acc = {}
    for p in paths[:n]:
        a, _ = raw(p)
        for k, v in planes(a).items():
            acc.setdefault(k, []).append(stat(v))
    return {k: float(np.mean(v)) for k, v in acc.items()}


bias = stack_planes(sorted(glob.glob(f"{B}/SW8_QHY294c_BIAS_40o_1600g/**/*.fits", recursive=True)), 20)
dflat = stack_planes(sorted(glob.glob(f"{B}/SW8_QHY294c_DARKFLAT_-5d_4s_40o_1600g/**/*.fits", recursive=True)), 20)
print(f"bias per channel (20 frames)    : " + "  ".join(f"{k}={v:.1f}" for k, v in sorted(bias.items())))
print(f"dark-flat per channel (20)      : " + "  ".join(f"{k}={v:.1f}" for k, v in sorted(dflat.items())))

# --- the flat, the measurement that was called ambiguous uncorrected -------------------------
fl = sorted(glob.glob(f"{S}/FLAT/**/*.fits", recursive=True))
rawf = stack_planes(fl, 10)
g_raw = (rawf["G"])
print(f"\nFLAT ({len(fl)} frames, 10 read)")
print(f"  raw       R/G {rawf['R']/g_raw:.4f}   B/G {rawf['B']/g_raw:.4f}")
corr = {k: rawf[k] - dflat[k] for k in rawf}
print(f"  corrected R/G {corr['R']/corr['G']:.4f}   B/G {corr['B']/corr['G']:.4f}"
      f"   (dark-flat subtracted)")

# --- the sky background, per target ------------------------------------------------------------
print(f"\nLIGHT background B/G per target, bias-corrected (background = per-channel 10th pct)")
by = {}
for p in sorted(glob.glob(f"{S}/LIGHT/**/*.fits", recursive=True)):
    a, h = raw(p)
    by.setdefault(str(h.get("OBJECT", "?")), []).append((p, a))
for obj, items in sorted(by.items()):
    mid = items[len(items) // 2 - 1: len(items) // 2 + 2]
    rr, bb = [], []
    for _, a in mid:
        pl = planes(a)
        sky = {k: np.percentile(v, 10) - bias[k] for k, v in pl.items()}
        rr.append(sky["R"] / sky["G"])
        bb.append(sky["B"] / sky["G"])
    bg = float(np.mean(bb))
    band = next((n for n, (lo, hi) in BANDS.items() if lo <= bg <= hi), "outside both")
    print(f"  {obj:24s} n={len(items):3d}  R/G {np.mean(rr):.4f}   B/G {bg:.4f}   -> {band}")
