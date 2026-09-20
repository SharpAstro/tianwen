"""Would FrameQualityFilter actually drop these frames? Run its own arithmetic over measured counts.

The gate is session-relative: threshold = median - sigma * 1.4826 * MAD on the star count, with a
cap on how much one pass may reject. So the question is not "are these frames bad" but "are they
OUTLIERS WITHIN THEIR OWN SESSION", which is a different question and, for a session that is mostly
bad, the opposite one.

Dataset bake passes sigma 3 and a 0.5 reject cap; `tianwen stack` leaves QualityRejectSigma null,
so the gate does not run there at all.
"""
import glob, os, warnings
import numpy as np
warnings.filterwarnings("ignore")
from astropy.io import fits

SIGMA, CAP = 3.0, 0.5


def star_counts(folder, limit=None):
    fs = sorted(glob.glob(os.path.join(folder, "**", "*.fits"), recursive=True))[:limit]
    out = []
    for f in fs:
        with fits.open(f, memmap=False) as h:
            a = h[0].data[0::2, 1::2].astype(np.float32)
        m = float(np.median(a))
        noise = float(np.median(np.abs(a - m)) * 1.4826)
        out.append(float((a > m + 8 * noise).sum()))
    return np.array(out), [os.path.basename(f) for f in fs]


def gate(counts, label):
    n = len(counts)
    med = float(np.sort(counts)[n // 2])
    mad = float(np.sort(np.abs(counts - med))[n // 2])
    thr = med - SIGMA * 1.4826 * mad
    flagged = counts < thr
    print(f"\n{label}")
    print(f"  n={n}  session median star count {med:.0f}  MAD {mad:.0f}  threshold {thr:.0f}")
    print(f"  frames below threshold: {int(flagged.sum())} of {n}"
          + (f"  (cap {CAP:.0%} would allow {int(CAP*n)})" if flagged.sum() else ""))
    lo = np.argsort(counts)[:6]
    print("  lowest counts: " + ", ".join(f"{int(counts[i])}" for i in lo))
    return flagged


c, _ = star_counts("D:/Astro-Pics/2026/2026-02-20 BAD LIGHT EXAMPLES")
f1 = gate(c, "CLOUD-OUT: 2026-02-20 BAD LIGHT EXAMPLES (24 of 33 clouded, stars 0.07 of the clear frames)")
print(f"  the two clear frames are {int(c[0])} and {int(c[1])}, ABOVE the median, so the gate sees")
print("  the clouded state as normal and the clear frames as the outliers.")

c2, names = star_counts("E:/Astro/SharpCap Captures/Helix Nebula RGB 120s -4deg 121g 11o/Light")
f2 = gate(c2, "POWERLINE: Helix 2022-08-31 (6 of 46 obstructed)")
print("  the six obstructed frames are 1 to 6: " + ", ".join(f"{int(x)}" for x in c2[:6]))
print(f"  of those, flagged by the gate: {int(f2[:6].sum())}")

c3, _ = star_counts("D:/Astro-Organized/lights/SVBONY-SV605CC/Optolong-L-Ultimate-3nm/Helix-Nebula/2026-08-01")
gate(c3, "ROOF: Helix 2026-08-01 (21 of 21 obstructed)")
print("  every frame carries the roof, so there is no in-session contrast for a relative test.")
