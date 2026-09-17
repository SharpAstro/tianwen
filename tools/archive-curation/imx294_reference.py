"""Same-SENSOR reference for group M's filter: the QHY294C PRO's IDAS-LPS-D3 flats (IMX294, 11M mode, RGGB),
measured exactly as group M's flat was (flat minus a dark-flat at the SAME exposure, centre 500x500 per
plane). Different camera bodies can still differ in the cover window's red response, so B/G is the
comparison that carries; R/G is shown but not leaned on. READ ONLY."""
import collections
import glob
import os
import numpy as np
import astropy.io.fits as fits

BASE = 'D:/Astro-Organized/flats/QHYCCD-QHY294C/IDAS-LPS-D3'


def by_exposure(ps):
    d = collections.defaultdict(list)
    for p in ps:
        d[round(float(fits.getheader(p).get('EXPTIME')), 3)].append(p)
    return d


def stack(ps, n=12):
    idx = np.linspace(0, len(ps) - 1, min(n, len(ps))).round().astype(int)
    return np.median(np.stack([fits.getdata(ps[i]).astype(np.float32) for i in idx]), axis=0)


def ratios(a):
    # SPLIT FIRST, crop the planes after. This frame is 2795 rows tall, so a centre crop taken on the
    # mosaic started on an odd row, swapped the rows, and read two greens as R and B: R/G and B/G came
    # out 2.4048 and 2.4042, the skill's own tell for a wrong phase.
    a = a[:a.shape[0] // 2 * 2, :a.shape[1] // 2 * 2]   # drop the odd last row so the planes align
    r, g, b = a[0::2, 0::2], 0.5 * (a[0::2, 1::2] + a[1::2, 0::2]), a[1::2, 1::2]
    h, w = g.shape
    sl = (slice(h // 2 - 250, h // 2 + 250), slice(w // 2 - 250, w // 2 + 250))
    r, g, b = r[sl], g[sl], b[sl]
    return np.median(r) / np.median(g), np.median(b) / np.median(g), np.median(g)


all_dark = {}
for d in sorted(glob.glob(BASE + '/*/')):
    for e, ps in by_exposure(glob.glob(d + 'DARKFLAT/*.fit*')).items():
        all_dark.setdefault(e, []).extend(ps)

for d in sorted(glob.glob(BASE + '/*/')):
    for e, ps in sorted(by_exposure(glob.glob(d + 'FLAT/*.fit*')).items()):
        dk = all_dark.get(e)
        if not dk:
            print(f'  {os.path.basename(os.path.normpath(d))} flat {e}s x{len(ps)}: no dark-flat at that exposure, skipped')
            continue
        rg, bg, g = ratios(stack(ps) - stack(dk))
        print(f'  {os.path.basename(os.path.normpath(d))} flat {e}s x{len(ps)} (dark-flat x{len(dk)})  R/G={rg:.4f}  B/G={bg:.4f}  G={g:.0f}')
print('\n  group M, ZWO ASI294MC 2024-02-03 (measured earlier)  R/G=0.5726  B/G=0.8088')
