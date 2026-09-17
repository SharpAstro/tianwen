"""What do the pre-NINA frames (no IMAGETYP) actually carry? READ ONLY.

For each (year, camera) sample a few frames of the unreachable-salvageable set and print the
software card, the keys present, and the folder path shape, so a classifier is designed off
the headers that exist rather than the ones we wish existed.
"""
import csv
import collections
import os
import random
import astropy.io.fits as fits

random.seed(1)
rows = [r for r in csv.DictReader(open(r'C:/temp/e2/archive-ledger.csv', encoding='utf-8'))
        if r['verdict'] == 'SALVAGEABLE' and r['reachable'] == 'False' and not r['imagetyp']]

groups = collections.defaultdict(list)
for r in rows:
    rel = os.path.relpath(r['path'], r'D:/Astro-Pics').replace(os.sep, '/')
    groups[(rel.split('/')[0], r['instrume'])].append(r)

sw = collections.Counter()
keysets = collections.Counter()
for (top, inst), rs in sorted(groups.items()):
    print(f'\n=== {top} / {inst}: {len(rs)} frames ===')
    exps = collections.Counter(r['exptime'] for r in rs)
    print('  EXPTIME:', ', '.join(f'{e}s x{c}' for e, c in exps.most_common(8)))
    # distinct parent-folder leaf names are the folder hint
    leaves = collections.Counter(os.path.basename(os.path.dirname(r['path'])) for r in rs)
    print('  folder leaves:', ', '.join(f'{l!r} x{c}' for l, c in leaves.most_common(10)))
    for r in random.sample(rs, min(2, len(rs))):
        h = fits.getheader(r['path'])
        soft = str(h.get('SWCREATE', h.get('CREATOR', h.get('PROGRAM', h.get('SOFTWARE', '')))))
        sw[(top, inst, soft)] += 1
        keys = [k for k in h.keys() if k not in ('SIMPLE', 'BITPIX', 'NAXIS', 'NAXIS1', 'NAXIS2',
                                                 'EXTEND', 'BZERO', 'BSCALE', 'COMMENT', 'HISTORY', '')]
        rel = os.path.relpath(r['path'], r'D:/Astro-Pics')
        print(f'  {rel}')
        print(f'    software={soft!r} dims={h.get("NAXIS1")}x{h.get("NAXIS2")} bitpix={h.get("BITPIX")}')
        print(f'    keys: {" ".join(keys)}')
