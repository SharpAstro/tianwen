"""Establish the SV605CC's own measurement constants before any filter verdict is attempted.

Read-only. Three things the ASI533 work needed and which do not transfer for free to a
different body, per docs/plans/filter-inference.md sections 3 and 8:

  1. bias, per (gain, offset), per CHANNEL, measured over the whole set rather than one frame
  2. the ADU scale (does this writer left-shift a 14-bit sensor into 16 bits, like N.I.N.A. does
     for the ASI533) -- section 6 phase F2
  3. the conversion gain in e-/ADU, measured from a FLAT PAIR photon-transfer relation, which is
     the textbook method and not the per-frame spatial fit that section 3 records as broken

B/G is a ratio, so (2) and (3) cancel out of the verdict entirely. They matter only for the sky
rate, which is corroboration. Measuring them anyway means the sky rate is this camera's number
and not the other camera's borrowed.
"""
import glob, math, os, re, sys, warnings
import numpy as np
warnings.filterwarnings("ignore")
from astropy.io import fits

BIAS_SETS = [
    ("gain120-off20 @ -9.8C", "D:/Astro-Pics/2025/2025-08-20 - Helix Nebula/BIAS"),
    ("gain120-off20 @ -4.3C", "D:/Astro-Pics/2026/2026-02-14 Statue of Liberty Nebula/2026-02-19/BIAS"),
]
FLAT_SETS = [
    ("Helix FLAT  6.39s @ -10.1C", "D:/Astro-Pics/2025/2025-08-20 - Helix Nebula/FLAT"),
    ("Ha-OIII FLAT 15s @ +16.8C", "D:/Astro-Pics/2025/2025-10/Ha-OIII Cal/FLAT"),
    ("RGB FLAT      7s @ +10.0C", "D:/Astro-Pics/2025/2025-10/RGB Cal/FLAT"),
]
LIGHT = "D:/Astro-Pics/2025/2025-08-20 - Helix Nebula/LIGHT"


def fits_in(d):
    return sorted(glob.glob(os.path.join(glob.escape(d), "*.fit*")))


def raw(path):
    """Physical ADU as written, with BZERO applied explicitly so the int16 storage of an
    unsigned sensor cannot read as negative."""
    with fits.open(path, memmap=False, do_not_scale_image_data=True) as hd:
        h = hd[0].header
        a = hd[0].data.astype(np.float64) + float(h.get("BZERO", 0.0)) * float(h.get("BSCALE", 1.0))
    return h, a


def planes2d(a, pat="RGGB"):
    """Per channel, the LIST of its CFA sub-planes, each still 2D. G has two sites in RGGB, and
    keeping them separate is what lets the gain measurement crop a central region per plane."""
    out = {}
    for i, ch in enumerate(pat):
        dy, dx = divmod(i, 2)
        out.setdefault(ch, []).append(a[dy::2, dx::2])
    return out


def planes(a, pat="RGGB"):
    """Per channel, all its samples flattened together. For medians only."""
    return {c: (v[0] if len(v) == 1 else np.concatenate([x.ravel() for x in v]))
            for c, v in planes2d(a, pat).items()}


print("=" * 96)
print("1. BIAS, per channel, over the whole set")
print("=" * 96)
print(f"{'set':<26}{'n used':>7}{'gain':>6}{'off':>5}{'tC':>7}{'R':>9}{'G':>9}{'B':>9}  grey?")
print("-" * 96)
for label, d in BIAS_SETS:
    fs = fits_in(d)
    if not fs:
        print(f"{label:<26}  MISSING: {d}")
        continue
    use = fs[:: max(1, len(fs) // 20)][:20]
    per = {"R": [], "G": [], "B": []}
    h0 = None
    for p in use:
        h, a = raw(p)
        h0 = h0 or h
        pl = planes(a, str(h.get("BAYERPAT", "RGGB")))
        for c in "RGB":
            per[c].append(float(np.median(pl[c])))
    R, G, B = (float(np.median(per[c])) for c in "RGB")
    grey = "yes" if max(abs(R - G), abs(B - G)) <= 2 else "NO"
    t = h0.get("CCD-TEMP")
    print(f"{label:<26}{len(use):>7}{str(h0.get('GAIN')):>6}{str(h0.get('OFFSET')):>5}"
          f"{(f'{float(t):.1f}' if t is not None else '?'):>7}{R:>9.1f}{G:>9.1f}{B:>9.1f}  {grey}")

print()
print("=" * 96)
print("2. ADU scale: is a 14-bit sensor left-shifted into 16 bits (values on a 4-ADU grid)?")
print("=" * 96)
print(f"{'source':<40}{'%div4':>8}{'%div16':>8}{'gcd':>6}  reading")
print("-" * 96)
for label, d in [("bias (gain120 -9.8C)", BIAS_SETS[0][1]), ("flat (Helix)", FLAT_SETS[0][1]),
                 ("light (Helix 60s)", LIGHT)]:
    fs = fits_in(d)
    if not fs:
        continue
    _h, a = raw(fs[len(fs) // 2])
    v = a.ravel().astype(np.int64)
    d4 = float(np.mean(v % 4 == 0)) * 100
    d16 = float(np.mean(v % 16 == 0)) * 100
    # gcd of a large sample of distinct values, after removing the pedestal
    s = np.unique(v[:: max(1, v.size // 200000)])
    g = 0
    for x in np.diff(s)[:5000]:
        g = math.gcd(g, int(x))
        if g == 1:
            break
    read = ("x4 scaled (14-bit sensor in 16-bit container)" if d4 > 90 else
            "x16 scaled (12-bit sensor)" if d16 > 90 else "unscaled / native")
    print(f"{label:<40}{d4:>8.1f}{d16:>8.1f}{g:>6}  {read}")

print()
print("=" * 96)
print("3. CONVERSION GAIN from consecutive FLAT PAIRS (mean-variance, per CFA channel)")
print("=" * 96)
print("   g = (signal above bias) / (variance of the pair difference / 2),  in e-/ADU")
print(f"\n{'flat set':<28}{'ch':>3}{'signal':>10}{'var/2':>11}{'g e-/ADU':>10}{'pairs':>7}")
print("-" * 96)
BIAS_ADU = {"R": None, "G": None, "B": None}
fs = fits_in(BIAS_SETS[0][1])
if fs:
    use = fs[:: max(1, len(fs) // 20)][:20]
    acc = {c: [] for c in "RGB"}
    for p in use:
        h, a = raw(p)
        pl = planes(a, str(h.get("BAYERPAT", "RGGB")))
        for c in "RGB":
            acc[c].append(float(np.median(pl[c])))
    BIAS_ADU = {c: float(np.median(acc[c])) for c in "RGB"}

gains = []
for label, d in FLAT_SETS:
    fs = fits_in(d)
    if len(fs) < 4:
        print(f"{label:<28}  too few frames ({len(fs)})")
        continue
    # consecutive pairs from the middle of the run, where panel output is steadiest
    mid = len(fs) // 2
    pairs = [(fs[i], fs[i + 1]) for i in range(max(0, mid - 6), min(len(fs) - 1, mid + 6), 2)]
    per = {c: [] for c in "RGB"}
    sig = {c: [] for c in "RGB"}
    for pa, pb in pairs:
        ha, A = raw(pa)
        hb, B = raw(pb)
        pat = str(ha.get("BAYERPAT", "RGGB"))
        PA, PB = planes2d(A, pat), planes2d(B, pat)
        for c in "RGB":
            for x2, y2 in zip(PA[c], PB[c]):
                x, y = x2.astype(np.float64), y2.astype(np.float64)
                # central region only, so vignetting gradient does not inflate the variance
                hh, ww = x.shape
                sl = (slice(hh // 4, 3 * hh // 4), slice(ww // 4, 3 * ww // 4))
                xc, yc = x[sl], y[sl]
                s = (float(np.mean(xc)) + float(np.mean(yc))) / 2 - (BIAS_ADU[c] or 0.0)
                v2 = float(np.var(xc - yc)) / 2.0
                if s > 50 and v2 > 0:
                    per[c].append(s / v2)
                    sig[c].append(s)
    for c in "RGB":
        if not per[c]:
            continue
        g = float(np.median(per[c]))
        print(f"{label:<28}{c:>3}{np.median(sig[c]):>10.0f}"
              f"{np.median(sig[c])/g:>11.0f}{g:>10.4f}{len(per[c]):>7}")
        gains.append(g)

if gains:
    g = float(np.median(gains))
    print(f"\n   median over all sets/channels: g = {g:.4f} e-/ADU at gain=120")
    print(f"   ASI533 model 0.7949*10^(-gain/200) would give "
          f"{0.7949 * 10 ** (-120 / 200.0):.4f} e-/ADU at gain=120")
    print(f"   ratio measured/model = {g / (0.7949 * 10 ** (-120 / 200.0)):.3f}")
