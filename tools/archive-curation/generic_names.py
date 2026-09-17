"""How much of 'reachable' rests on a generic SharpCap name matched by name+size alone? READ ONLY.

SharpCap restarts frame_00001.fits in every rawframes folder, and one camera at one ROI writes the
same size every time, so (name, size) can match a DIFFERENT capture. Inode matching is immune.
"""
import csv
import collections
import os
import re

GENERIC = re.compile(r'^(frame_\d+|capture_\d+|.*_\d{5}_?)\.fits?$', re.IGNORECASE)
ROOTS = [r'D:/Astro-Organized', r'D:/Astro-Unsorted']

ino = set()
for root in ROOTS:
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d.lower() not in {'proc', 'int', '$recycle.bin'}]
        for f in files:
            if f.lower().endswith(('.fit', '.fits', '.fts')):
                try:
                    ino.add(os.stat(os.path.join(base, f)).st_ino)
                except OSError:
                    pass

c = collections.Counter()
for r in csv.DictReader(open(r'C:/temp/e2/archive-ledger.csv', encoding='utf-8')):
    if r['verdict'] != 'SALVAGEABLE' or r['reachable'] != 'True':
        continue
    name = os.path.basename(r['path'])
    by_inode = os.stat(r['path']).st_ino in ino
    c[('inode' if by_inode else 'name+size only',
       'generic name' if GENERIC.match(name) else 'distinct name')] += 1

for k, v in sorted(c.items()):
    print(f'  {k[0]:16s} {k[1]:14s} {v}')
