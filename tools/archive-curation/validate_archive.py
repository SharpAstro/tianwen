"""Does Astro-Organized contain everything SALVAGEABLE from Astro-Pics?

Emits a per-frame ledger with one verdict each, so the answer is auditable rather than a
number someone has to trust. Three earlier surveys of this archive gave three different
answers; each failure mode below is one of them, made explicit so it cannot recur.

SALVAGEABLE means: a RAW CAPTURE a bake could actually use. Judged by several fields
together, never by one, because every single field fails somewhere in this archive:

  IMAGETYP      ABSENT on the entire 2021-2023 archive. A "not LIGHT means calibration"
                rule reported 0 lights and 17,342 calibration frames for 2021, when M83
                2022's 23 frames at 60s are plainly lights. Usable when present, never
                required.
  EXPTIME       Stacked integrations keep IMAGETYP=LIGHT and carry 9000 to 24120 s. They
                are products. A raw sub is seconds to minutes.
  filename      Masters and by-products are not reliably named. astrometry.net drops
                _axy and _image-radec FITS beside the frames it solves.
  INSTRUME      The most reliable single signal: a real capture records its camera, and
                every product checked either omits it or writes 'notAvailable'.

REACHABILITY is inode OR (name, size), per the curate-session rule: Unsorted is mostly
hard links back to Astro-Pics while Organized is a real copy, so either test alone
under-reports. Where name and size collide but content might differ, the pair is listed
for hashing rather than assumed identical.

READ ONLY. Writes only under C:/temp/e2.
"""
import os
import csv
import collections
import astropy.io.fits as fits

PICS = r'D:/Astro-Pics'
ROOTS = [r'D:/Astro-Organized', r'D:/Astro-Unsorted']
OUT = r'C:/temp/e2/archive-ledger.csv'
FITS_EXT = ('.fit', '.fits', '.fts', '.fit.fz', '.fits.fz')

# Directories that never hold a usable capture.
SKIP_DIR_EXACT = {'proc', 'int', '$recycle.bin', 'system volume information'}
SKIP_DIR_SUBSTR = ('bad light',)
# astrometry.net and similar by-products, by name.
ARTEFACT_MARKERS = ('_axy', '-radec', '.corr', '.rdls', '.match', '-indx', '.xyls',
                    '.wcs', '.new', '.solved')
# IMAGETYP values that are products rather than captures.
PRODUCT_TYPES = ('MASTER', 'BADPIXEL', 'CALIBRATED', 'STACK', 'INTEGRAT')
MAX_SUB_SECONDS = 1800.0


def is_fits(n):
    return n.lower().endswith(FITS_EXT)


def build_reachable():
    ino, ns = set(), set()
    for root in ROOTS:
        n = 0
        for base, dirs, files in os.walk(root):
            dirs[:] = [d for d in dirs if d.lower() not in SKIP_DIR_EXACT]
            for f in files:
                if not is_fits(f):
                    continue
                try:
                    st = os.stat(os.path.join(base, f))
                except OSError:
                    continue
                ino.add(st.st_ino)
                ns.add((f.lower(), st.st_size))
                n += 1
        print(f'  indexed {root}: {n} FITS', flush=True)
    return ino, ns


def classify(path, name, dirparts):
    """-> (verdict, reason, meta) where verdict is SALVAGEABLE or NOT-SALVAGEABLE."""
    low = name.lower()
    if any(m in low for m in ARTEFACT_MARKERS):
        return 'NOT-SALVAGEABLE', 'solver-artefact', {}
    for p in dirparts:
        pl = p.lower()
        if pl in SKIP_DIR_EXACT or any(s in pl for s in SKIP_DIR_SUBSTR):
            return 'NOT-SALVAGEABLE', f'excluded-dir:{p}', {}
    try:
        h = fits.getheader(path)
    except Exception:
        return 'NOT-SALVAGEABLE', 'unreadable', {}
    inst = str(h.get('INSTRUME', '') or '').strip()
    typ = str(h.get('IMAGETYP', '') or '').strip()
    try:
        exp = float(h.get('EXPTIME', 0) or 0)
    except Exception:
        exp = 0.0
    meta = {'instrume': inst, 'imagetyp': typ, 'exptime': round(exp, 2),
            'object': str(h.get('OBJECT', '') or '').strip(),
            'date': str(h.get('DATE-OBS', '') or '')[:10],
            'gain': h.get('GAIN'), 'filter': str(h.get('FILTER', '') or '').strip()}
    if not inst or inst.lower() in ('notavailable', 'n/a', 'none'):
        return 'NOT-SALVAGEABLE', 'no-camera', meta
    if any(p in typ.upper() for p in PRODUCT_TYPES):
        return 'NOT-SALVAGEABLE', f'product:{typ}', meta
    if exp > MAX_SUB_SECONDS:
        return 'NOT-SALVAGEABLE', f'integration:{exp:g}s', meta
    return 'SALVAGEABLE', '', meta


def main():
    print('indexing what is already reachable...', flush=True)
    ino, ns = build_reachable()
    print(f'  {len(ino)} inodes, {len(ns)} name+size pairs', flush=True)

    counts = collections.Counter()
    reasons = collections.Counter()
    missing = collections.defaultdict(lambda: {'n': 0, 'gib': 0.0})
    n = 0
    with open(OUT, 'w', newline='', encoding='utf-8') as fh:
        w = csv.writer(fh)
        w.writerow(['path', 'verdict', 'reason', 'reachable', 'instrume', 'imagetyp',
                    'exptime', 'object', 'date', 'gain', 'filter', 'size'])
        for base, dirs, files in os.walk(PICS):
            dirs[:] = [d for d in dirs
                       if d.lower() not in SKIP_DIR_EXACT
                       and not any(s in d.lower() for s in SKIP_DIR_SUBSTR)]
            rel = os.path.relpath(base, PICS)
            parts = [] if rel == '.' else rel.split(os.sep)
            for f in files:
                if not is_fits(f):
                    continue
                p = os.path.join(base, f)
                try:
                    st = os.stat(p)
                except OSError:
                    continue
                n += 1
                if n % 2000 == 0:
                    print(f'  {n} frames judged...', flush=True)
                verdict, reason, meta = classify(p, f, parts)
                reach = st.st_ino in ino or (f.lower(), st.st_size) in ns
                counts[(verdict, reach)] += 1
                if verdict == 'NOT-SALVAGEABLE':
                    reasons[reason.split(':')[0]] += 1
                elif not reach:
                    bucket = os.sep.join(parts[:2]) if parts else '(root)'
                    missing[bucket]['n'] += 1
                    missing[bucket]['gib'] += st.st_size / 2**30
                w.writerow([p, verdict, reason, reach, meta.get('instrume', ''),
                            meta.get('imagetyp', ''), meta.get('exptime', ''),
                            meta.get('object', ''), meta.get('date', ''),
                            meta.get('gain', ''), meta.get('filter', ''), st.st_size])

    print()
    print(f'FITS under Astro-Pics: {n}')
    print()
    print('%-18s %-12s %s' % ('verdict', 'reachable', 'frames'))
    for (v, r), c in sorted(counts.items()):
        print('%-18s %-12s %d' % (v, r, c))
    print()
    print('NOT-SALVAGEABLE by reason:')
    for r, c in reasons.most_common():
        print(f'   {r:22s} {c}')
    gap = counts[('SALVAGEABLE', False)]
    print()
    print(f'>>> SALVAGEABLE BUT NOT REACHABLE: {gap} frames '
          f'({sum(v["gib"] for v in missing.values()):.1f} GiB)  <<<')
    print()
    for b, v in sorted(missing.items(), reverse=True)[:30]:
        print('   %-34s %6d frames  %7.1f GiB' % (b[:34], v['n'], v['gib']))
    print()
    print('ledger:', OUT)


if __name__ == '__main__':
    main()
