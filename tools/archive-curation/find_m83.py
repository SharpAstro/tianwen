"""Find M83 by FITS header OBJECT, not by folder name (see task #25).

Samples ONE header per directory that holds FITS, which is enough to identify a session's
target and instrument without opening thousands of frames.
"""
import os
import astropy.io.fits as fits

ROOTS = [r'D:/Astro-Pics', r'D:/Astro-Unsorted', r'D:/Astro-Organized']
NEEDLES = ('m83', 'm 83', '5236', 'pinwheel', 'spw')

hits = []
scanned = 0
for root in ROOTS:
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if not d.startswith('.')]
        fit = [f for f in files if f.lower().endswith(('.fit', '.fits', '.fit.fz', '.fits.fz'))]
        if not fit:
            continue
        scanned += 1
        p = os.path.join(base, sorted(fit)[0])
        try:
            h = fits.getheader(p)
        except Exception:
            continue
        obj = str(h.get('OBJECT', '') or '')
        inst = str(h.get('INSTRUME', '') or '')
        tel = str(h.get('TELESCOP', '') or '')
        hay = f'{obj} {os.path.basename(base)}'.lower()
        if any(n in hay for n in NEEDLES):
            hits.append((base, obj, inst, tel, str(h.get('DATE-OBS', '')), str(h.get('FOCALLEN', '')), len(fit)))

print(f'directories holding FITS scanned: {scanned}')
print(f'M83-like: {len(hits)}')
for base, obj, inst, tel, dt, fl, n in sorted(hits, key=lambda r: r[4]):
    print(f'  {dt[:10]}  {inst:22s} fl={fl:6s} n={n:5d}  OBJECT={obj[:34]:34s} {base}')

# Also: anything at all shot with a QHY294, so the reshoot is visible even if OBJECT is odd.
print()
print('=== every QHY294 directory found (by INSTRUME) ===')
q = []
for root in ROOTS:
    for base, dirs, files in os.walk(root):
        fit = [f for f in files if f.lower().endswith(('.fit', '.fits', '.fit.fz', '.fits.fz'))]
        if not fit:
            continue
        try:
            h = fits.getheader(os.path.join(base, sorted(fit)[0]))
        except Exception:
            continue
        if '294' in str(h.get('INSTRUME', '')):
            q.append((str(h.get('DATE-OBS', ''))[:10], str(h.get('OBJECT', '')), str(h.get('FOCALLEN', '')), len(fit), base))
for dt, obj, fl, n, base in sorted(q):
    print(f'  {dt}  fl={fl:6s} n={n:5d}  OBJECT={obj[:34]:34s} {base}')
