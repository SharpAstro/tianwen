"""Which filter ran on the 2025-05-25 ASI585 night? No frame carries a FILTER card.

Two independent measurements, as filter-inference.md prescribes:

  (a) the FLAT's channel ratios, which carry the passband with none of the sky's confounds
  (b) the bias-corrected SKY background B/G on the raw undebayered lights, which is the
      discriminator the doc names (R/G is useless, the populations overlap)

BAYERPAT here is RGGB, NOT the GRBG of the SV605CC: R G / G B. Forcing the wrong phase
samples two greens as R and B and the tell is R/G and B/G agreeing to four figures, so the
phase is read from the header and asserted, never assumed.

Comparison is WITHIN the ASI585 only. The reference bands in the doc are IMX533-derived and
do not transfer between sensors.

READ ONLY.
"""
import glob
import numpy as np
import astropy.io.fits as fits

ORG = r'D:/Astro-Organized'
NEW = r'D:/Astro-Pics/2025/2025-05-25 - Rim Nebula'


def split_rggb(a):
    """RGGB: R G / G B."""
    r = a[0::2, 0::2]
    g = 0.5 * (a[0::2, 1::2] + a[1::2, 0::2])
    b = a[1::2, 1::2]
    return r, g, b


def check_phase(paths, label):
    """If R/G and B/G agree to 4 figures we sampled two greens: the phase is wrong."""
    a = fits.getdata(paths[0]).astype(np.float64)
    r, g, b = split_rggb(a)
    rg, bg = np.median(r) / np.median(g), np.median(b) / np.median(g)
    flag = ' <-- SUSPECT: R/G == B/G, wrong CFA phase?' if abs(rg - bg) < 1e-4 else ''
    print(f'  phase check {label}: R/G={rg:.4f} B/G={bg:.4f}{flag}')


def med_stack(paths, n):
    return np.median(np.stack([fits.getdata(p).astype(np.float64) for p in paths[:n]]), axis=0)


def ratios(frames, bias=None, central=True, label=''):
    a = frames if bias is None else frames - bias
    r, g, b = split_rggb(a)
    if central:
        h, w = g.shape
        sl = (slice(h // 2 - 250, h // 2 + 250), slice(w // 2 - 250, w // 2 + 250))
        r, g, b = r[sl], g[sl], b[sl]
    rr, gg, bb = np.median(r), np.median(g), np.median(b)
    return rr / gg, bb / gg, gg


print('=== (a) FLAT channel ratios, bias-corrected where a bias exists ===')
newbias = med_stack(sorted(glob.glob(NEW + '/BIAS/*.fit*')), 30)
check_phase(sorted(glob.glob(NEW + '/FLAT/*.fit*')), 'new flat')
newflat = med_stack(sorted(glob.glob(NEW + '/FLAT/*.fit*')), 20)
rg, bg, lvl = ratios(newflat, newbias)
print(f'  {"2025-05-25 UNKNOWN (group K)":42s} R/G={rg:.4f}  B/G={bg:.4f}  G={lvl:7.0f}')

refs = [
    ('Optolong-L-eNhance', '2024-09-28', f'{ORG}/calibration/ZWO-ASI585MC-Pro/BIAS/2024-09-28-g252-o7-t-10'),
    ('Unidentified-Broadband', '2024-10-03', None),
    ('Unidentified-Broadband', '2025-02-01', f'{ORG}/calibration/ZWO-ASI585MC-Pro/BIAS/2025-02-01-g252-o3-t+18'),
]
for filt, date, biasdir in refs:
    fl = sorted(glob.glob(f'{ORG}/flats/ZWO-ASI585MC-Pro/{filt}/{date}/FLAT/*.fit*'))
    if not fl:
        continue
    b = med_stack(sorted(glob.glob(biasdir + '/*.fit*')), 30) if biasdir else None
    rg, bg, lvl = ratios(med_stack(fl, 20), b)
    tag = '' if b is not None else '  (no bias available, raw)'
    print(f'  {filt + " " + date:42s} R/G={rg:.4f}  B/G={bg:.4f}  G={lvl:7.0f}{tag}')

print()
print('=== (b) bias-corrected SKY background B/G on the raw lights ===')
for lbl, g in [('Rim Nebula 2025-05-25 (group K)', NEW + '/LIGHT/*.fit*'),
               ('Lagoon Nebula 2025-05-25 (group K)',
                r'D:/Astro-Pics/2025/2025-05-25 - Lagoon Nebula/LIGHT/*.fit*')]:
    fs = sorted(glob.glob(g))
    if not fs:
        continue
    # A background statistic, so take a low percentile per channel rather than the median of a
    # frame that contains a nebula.
    vals = []
    for p in fs[::max(1, len(fs) // 12)][:12]:
        a = fits.getdata(p).astype(np.float64) - newbias
        r, gr, b = split_rggb(a)
        vals.append((np.percentile(r, 20), np.percentile(gr, 20), np.percentile(b, 20)))
    r, gr, b = np.median([v[0] for v in vals]), np.median([v[1] for v in vals]), np.median([v[2] for v in vals])
    print(f'  {lbl:42s} R/G={r / gr:.4f}  B/G={b / gr:.4f}  (sky p20, bias-corrected)')
