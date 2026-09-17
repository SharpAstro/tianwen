"""Where does a master flat's "no information" population end and real vignette begin?

Master flats are normalised to mean 1 per frame before the median combine, so the value IS the
relative throughput. A dust mote or a vignette corner is a real optical attenuation and lands in
tenths; a shielded, dead or masked pixel carries no light at all and lands near zero. If the two
populations are separated by a wide empty band, the threshold can be measured rather than chosen.
"""
import glob
import os
import numpy as np
from astropy.io import fits

BARS = (0.0, 1e-6, 1e-4, 1e-3, 0.01, 0.02, 0.05, 0.10, 0.20, 0.30)
root = "D:/Astro-Dataset/2026-09-12-clamped/masters"
print(f"{'flat':58s} {'median':>8} " + " ".join(f"<{b:g}" for b in BARS[1:]))
totals = np.zeros(len(BARS))
affected = []
for path in sorted(glob.glob(os.path.join(root, "master_flat_*.fits"))):
    with fits.open(path, memmap=False) as hdul:
        hdu = next(h for h in hdul if h.data is not None and h.data.ndim >= 2)
        d = np.asarray(hdu.data, dtype=np.float32)
    v = d[np.isfinite(d)]
    med = float(np.median(v))
    counts = [int((v <= b).sum()) for b in BARS]
    name = os.path.basename(path)[12:70]
    print(f"{name:58s} {med:8.4f} " + " ".join(f"{c:9,d}" for c in counts[1:]))
    totals += np.array(counts, dtype=float)
    if counts[4] > 0:      # any pixel at or under 0.01 of the mean
        affected.append((os.path.basename(path), counts[0], counts[4]))

print(f"\n{'TOTAL':58s} {'':8s} " + " ".join(f"{int(c):9,d}" for c in totals[1:]))
print(f"\nexactly zero across all flats: {int(totals[0]):,}")
print("\nflats with any pixel at or under 0.01 of the mean (these are the ones a threshold touches):")
for n, z, c in affected:
    print(f"  {c:9,d} px  (of which {z:,} exactly 0)  {n}")
