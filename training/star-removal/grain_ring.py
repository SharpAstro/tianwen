"""Did the grain rule leak into a plate's ZERO canvas ring? (R4f's export failed three sessions on it, 2026-11-20.)
    python -I grain_ring.py <stored plates> <grain plates> <out tsv> [<session list> ...]
plate_grain.py keeps NaN absent, but a plate whose master is not on the unit scale stores its canvas ring as exact zeros,
and the product reads an all-zero pixel as absent. Per plate: the ring (every channel exactly zero in the stored plate),
how many ring pixels the rule's plate holds as something else, the rule's most negative value, and the valid pixels within
20 px of the ring that the rule lowered by over 10 % of their value. The lists mark which sessions train or validate."""
import glob, os, sys
import numpy as np
from astropy.io import fits
from scipy.ndimage import binary_dilation

A, B, OUT = sys.argv[1:4]
named = set()
for p in sys.argv[4:]:
    named |= {l.strip() for l in open(p, encoding="utf-8-sig") if l.strip() and not l.startswith("#")}
slug = {s.replace("/", "_").replace("|", "_") for s in named}
out = open(OUT, "w", encoding="utf-8")
out.write("ring_px\tring_changed\tmin\tnear_lowered\tlisted\tstem\n")
tot = bad = 0
for path in sorted(glob.glob(os.path.join(A, "*_plate.fits"))):
    stem = os.path.basename(path)[:-len("_plate.fits")]
    other = os.path.join(B, stem + "_plate.fits")
    if not os.path.exists(other):
        continue
    a = np.asarray(fits.getdata(path), np.float32); a = a[None] if a.ndim == 2 else a
    b = np.asarray(fits.getdata(other), np.float32); b = b[None] if b.ndim == 2 else b
    ring = (a == 0).all(axis=0)
    n_ring = int(ring.sum())
    changed = int(((b != 0).any(axis=0) & ring).sum()) if n_ring else 0
    near = lowered = 0
    if n_ring:
        band = binary_dilation(ring, iterations=20) & ~ring
        with np.errstate(invalid="ignore", divide="ignore"):
            lowered = int(((b < 0.9 * a).any(axis=0) & band & np.isfinite(a).all(axis=0)).sum())
    tot += 1; bad += changed > 0
    listed = "listed" if stem in slug else ""
    out.write(f"{n_ring}\t{changed}\t{float(np.nanmin(b)):.6g}\t{lowered}\t{listed}\t{stem}\n"); out.flush()
print(f"plates read {tot}; with ring pixels the rule changed: {bad}")
