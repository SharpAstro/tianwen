"""The validator ledger rounded EXPTIME to two decimals, so a sub-5 ms capture (a Moon frame, a
planetary run) reads 0.0 and passes for a bias. Re-read the exact card for every frame #34 samples.
READ ONLY; writes C:/temp/e2/cache-exptime.json keyed by frame path."""
import csv
import json
import os
import astropy.io.fits as fits

GAP = r'C:/temp/e2/stage0-gap.csv'
OUT = r'C:/temp/e2/cache-exptime.json'

rows = [r for r in csv.DictReader(open(GAP, encoding='utf-8')) if not r['stage0'].startswith('PRODUCT')]
sets = {}
for r in rows:
    k = (r['stage0'], os.path.dirname(r['path']), r['instrume'], r['exptime'], r['gain'], r['naxis1'], r['naxis2'])
    sets.setdefault(k, []).append(r['path'])

out = {}
for k, paths in sets.items():
    paths.sort(key=str.lower)
    for p in ([paths[0], paths[len(paths) // 2], paths[-1]] if len(paths) >= 3 else paths):
        h = fits.getheader(p)
        v = h.get('EXPTIME', h.get('EXPOSURE'))
        t = h.get('CCD-TEMP')
        out[p] = {'exptime': None if v is None else float(v), 'ccd_temp': None if t is None else float(t)}
json.dump(out, open(OUT, 'w', encoding='utf-8'), indent=0)

print(f"{sum(1 for v in out.values() if v['ccd_temp'] is not None)} of {len(out)} sampled frames carry CCD-TEMP")
zero_rounded = {p: v['exptime'] for p, v in out.items() if v['exptime'] is not None and 0 < v['exptime'] < 0.005}
print(f'{len(out)} sampled frames; {len(zero_rounded)} have an exposure the ledger rounded to 0.0:')
for p, v in sorted(zero_rounded.items()):
    print(f'  {v:.6f}s  {os.path.relpath(p, "D:/Astro-Pics")}')
print('exactly zero:', sum(1 for v in out.values() if v == 0.0))
