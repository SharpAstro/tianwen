"""Would FrameQualityFilter actually drop these frames? Run its own arithmetic over measured counts.

The gate is session-relative: threshold = median - sigma * 1.4826 * MAD on the star count, with a
cap on how much one pass may reject. So the question is not "are these frames bad" but "are they
OUTLIERS WITHIN THEIR OWN SESSION", which is a different question and, for a session that is mostly
bad, the opposite one.

Dataset bake passes sigma 3 and a 0.5 reject cap; `tianwen stack` leaves QualityRejectSigma null,
so the gate does not run there at all.

The count is a PROXY: photosites above the frame's median plus eight sigma, on one colour of the
mosaic, the same crude number band_stars.py uses. It is not the registration detector's star list,
so the medians and MADs printed here are not the ones the real gate would compute; what carries over
is which SIDE of the session median a defect puts its frames on, and a proxy that moves with the
star count puts them on the same side.

Usage: python would_the_gate_fire.py "<folder of lights>" ["<folder>" ...] [--sigma 3] [--cap 0.5]
"""
import argparse
import glob
import os
import warnings

import numpy as np

warnings.filterwarnings("ignore")
from astropy.io import fits  # noqa: E402


def star_counts(folder):
    fs = sorted(glob.glob(os.path.join(folder, "**", "*.fits"), recursive=True))
    out = []
    for f in fs:
        with fits.open(f, memmap=False) as h:
            a = h[0].data[0::2, 1::2].astype(np.float32)
        m = float(np.median(a))
        noise = float(np.median(np.abs(a - m)) * 1.4826)
        out.append(float((a > m + 8 * noise).sum()))
    return np.array(out), [os.path.basename(f) for f in fs]


def gate(counts, names, sigma, cap):
    n = len(counts)
    med = float(np.sort(counts)[n // 2])
    mad = float(np.sort(np.abs(counts - med))[n // 2])
    thr = med - sigma * 1.4826 * mad
    flagged = counts < thr
    print(f"  n={n}  session median proxy count {med:.0f}  MAD {mad:.0f}  threshold {thr:.0f}")
    print(f"  frames below threshold: {int(flagged.sum())} of {n}"
          + (f"  (cap {cap:.0%} would allow {int(cap * n)})" if flagged.sum() else ""))
    order = np.argsort(counts)
    print("  lowest counts: " + ", ".join(f"{names[i]} {int(counts[i])}" for i in order[:6]))
    print("  highest counts: " + ", ".join(f"{names[i]} {int(counts[i])}" for i in order[-3:]))
    if flagged.any():
        print("  flagged: " + ", ".join(names[i] for i in np.nonzero(flagged)[0][:12])
              + (" ..." if flagged.sum() > 12 else ""))
    if thr < 0:
        print("  the threshold is NEGATIVE: the session's median IS the defective state, so no frame "
              "can fall below it and the clear frames are the outliers, on the side nothing tests")
    return flagged


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("folders", nargs="+", help="folders of lights, each a session")
    p.add_argument("--sigma", type=float, default=3.0, help="the bake's QualityRejectSigma (default 3)")
    p.add_argument("--cap", type=float, default=0.5, help="the bake's QualityMaxRejectFraction (default 0.5)")
    a = p.parse_args()
    for folder in a.folders:
        counts, names = star_counts(folder)
        print(f"\n{folder}")
        if len(counts) == 0:
            print("  no FITS found")
            continue
        gate(counts, names, a.sigma, a.cap)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
