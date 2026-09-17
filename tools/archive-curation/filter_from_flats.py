"""Two questions about the SV605CC.

1. Is there a calibration set CLOSER to 2026-08-01 than the ones group J inherits, anywhere
   in Astro-Pics (not just what has already been filed)?
2. Do the FLATS reveal which filter was in the manual holder?

For (2) the CFA is GRBG, so in each 2x2 block: [0,0]=G [0,1]=R [1,0]=B [1,1]=G. Sampling the
wrong phase silently reads two greens as R and B, and the tell is R/G and B/G coming out equal
to four figures -- so the phase is asserted here from the header, never assumed.

A flat carries the passband with none of the sky's confounds, which is why it is the better
discriminator. The comparison is WITHIN one camera: these ratios are sensor-specific and do not
transfer to another body.

READ ONLY.
"""
import glob
import os
import collections
import numpy as np
import astropy.io.fits as fits

PICS = r'D:/Astro-Pics'
ORG = r'D:/Astro-Organized'


def hdr(p):
    with fits.open(p, memmap=False) as h:
        x = h[0].header
        return (str(x.get('INSTRUME', '')), str(x.get('IMAGETYP', '')),
                str(x.get('DATE-OBS', ''))[:10], str(x.get('FILTER', '')),
                x.get('GAIN'), x.get('OFFSET'), float(x.get('EXPTIME', 0) or 0),
                str(x.get('BAYERPAT', '')))


print('=== 1. SV605CC calibration anywhere in Astro-Pics, by date ===')
found = collections.defaultdict(lambda: collections.Counter())
for base, dirs, files in os.walk(PICS):
    dirs[:] = [d for d in dirs if d.lower() not in ('proc', '$recycle.bin')]
    fitsfiles = [f for f in files if f.lower().endswith(('.fit', '.fits'))]
    if not fitsfiles:
        continue
    try:
        inst, typ, date, filt, g, o, exp, bp = hdr(os.path.join(base, sorted(fitsfiles)[0]))
    except Exception:
        continue
    if 'SV605CC' not in inst.upper().replace(' ', ''):
        continue
    t = typ.upper()
    if 'LIGHT' in t:
        continue
    found[(date, t, filt, g, o, round(exp, 2))][base] = len(fitsfiles)
for k in sorted(found):
    date, t, filt, g, o, exp = k
    n = sum(found[k].values())
    print(f'  {date}  {t:12s} filt={filt[:24]:24s} g={g} o={o} exp={exp:g}s  n={n}')

print()
print('=== 2. flat channel ratios per filter (same camera) ===')


def ratios(paths, n=12):
    r, g, b = [], [], []
    for p in paths[:n]:
        a = fits.getdata(p).astype(np.float64)
        # GRBG: G R / B G
        g0 = a[0::2, 0::2]
        rr = a[0::2, 1::2]
        bb = a[1::2, 0::2]
        g1 = a[1::2, 1::2]
        gg = 0.5 * (g0 + g1)
        # central region only: vignetting and the OAG shadow are strongest at the edges and
        # would bias a whole-frame mean differently per set.
        h, w = gg.shape
        sl = (slice(h // 2 - 300, h // 2 + 300), slice(w // 2 - 300, w // 2 + 300))
        r.append(np.median(rr[sl]))
        g.append(np.median(gg[sl]))
        b.append(np.median(bb[sl]))
    r, g, b = np.median(r), np.median(g), np.median(b)
    return r / g, b / g, g


for filt_dir in sorted(glob.glob(ORG + '/flats/SVBONY-SV605CC/*')):
    filt = os.path.basename(filt_dir)
    for date_dir in sorted(glob.glob(filt_dir + '/*')):
        flats = sorted(glob.glob(date_dir + '/FLAT/*.fits'))
        if not flats:
            continue
        rg, bg, glvl = ratios(flats)
        # Read the card the frames themselves carry, which may differ from the directory.
        card = hdr(flats[0])[3]
        print(f'  {filt:26s} {os.path.basename(date_dir)}  n={len(flats):3d}  '
              f'R/G={rg:.4f}  B/G={bg:.4f}  Glevel={glvl:8.0f}  card={card[:26]!r}')
