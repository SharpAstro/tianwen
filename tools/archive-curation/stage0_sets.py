"""Group stage-0's gap frames into capture sets, to size the pixel pass. READ ONLY."""
import csv
import collections
import os

sets = collections.defaultdict(lambda: {'n': 0, 'gib': 0.0, 'types': collections.Counter()})
for r in csv.DictReader(open(r'C:/temp/e2/stage0-gap.csv', encoding='utf-8')):
    if r['stage0'].startswith('PRODUCT'):
        continue
    key = (r['stage0'], os.path.dirname(r['path']), r['instrume'], r['exptime'], r['gain'],
           r['naxis1'], r['naxis2'])
    s = sets[key]
    s['n'] += 1
    s['gib'] += int(r['size']) / 2**30
    s['types'][r['type']] += 1

for stage in ('TYPED', 'UNTYPED'):
    ks = [k for k in sets if k[0] == stage]
    mixed = [k for k in ks if len(sets[k]['types']) > 1]
    print(f'{stage}: {len(ks)} capture sets, {sum(sets[k]["n"] for k in ks)} frames, '
          f'{len(mixed)} sets whose frames disagree on type')

print('\nUNTYPED sets (folder relative to Astro-Pics, camera, exposure, gain, dims, frames):')
for k in sorted((k for k in sets if k[0] == 'UNTYPED'), key=lambda k: k[1]):
    rel = os.path.relpath(k[1], r'D:/Astro-Pics')
    print(f'  {rel[:78]:78s} {k[2][:12]:12s} {k[3]:>6s}s g{k[4]:<4s} {k[5]}x{k[6]} n={sets[k]["n"]}')
