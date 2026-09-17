"""Classify every shared session master: did it change, in what units, with what NaN.

Subsamples rows for speed (every 5th row) -- enough to detect ANY change and to read the
unit convention; a master that differs anywhere almost certainly differs in the sample, and
the few that do not are re-checked in full by the caller.
"""
import os
import numpy as np
import astropy.io.fits as fits
import json

A = r'D:/Astro-Dataset/2026-09-12-clamped/session-masters'
B = r'D:/Astro-Dataset/2026-09-16-flatfloor/session-masters'
STEP = 5

rows = []
common = sorted(set(os.listdir(A)) & set(os.listdir(B)))
for i, n in enumerate(common, 1):
    try:
        a = fits.getdata(os.path.join(A, n))
        b = fits.getdata(os.path.join(B, n))
    except Exception as e:
        rows.append({'name': n, 'error': str(e)[:80]})
        continue
    if a.shape != b.shape:
        rows.append({'name': n, 'shape_old': str(a.shape), 'shape_new': str(b.shape), 'changed': True, 'reason': 'shape'})
        del a, b
        continue
    # Subsample along the last axis's rows.
    sl = (Ellipsis, slice(None, None, STEP), slice(None))
    aa = np.asarray(a[sl], dtype=np.float64)
    bb = np.asarray(b[sl], dtype=np.float64)
    del a, b
    fin = np.isfinite(aa) & np.isfinite(bb)
    nan_a = int(np.sum(~np.isfinite(aa)))
    nan_b = int(np.sum(~np.isfinite(bb)))
    d = np.abs(aa - bb)
    ndiff = int(np.sum((d > 0) & fin))
    peak = float(np.nanmax(aa[np.isfinite(aa)])) if np.isfinite(aa).any() else float('nan')
    # Relative move: ratio where the old is meaningfully non-zero.
    m = fin & (np.abs(aa) > (peak * 1e-6 if np.isfinite(peak) and peak > 0 else 0))
    if m.any() and ndiff:
        r = bb[m] / aa[m]
        relp50 = float(np.percentile(r, 50))
        relp1 = float(np.percentile(r, 1))
        relp99 = float(np.percentile(r, 99))
    else:
        relp50 = relp1 = relp99 = 1.0
    rows.append({
        'name': n,
        'changed': ndiff > 0,
        'diff_frac': ndiff / max(int(np.sum(fin)), 1),
        'peak_old': peak,
        'units': 'normalised' if np.isfinite(peak) and peak <= 4.0 else 'adu',
        'nan_old': nan_a, 'nan_new': nan_b,
        'rel_p1': relp1, 'rel_p50': relp50, 'rel_p99': relp99,
    })
    del aa, bb, d, fin
    print(f'{i}/{len(common)} {n[:58]}', flush=True)

with open(r'C:/temp/e2/divergence.json', 'w') as f:
    json.dump(rows, f, indent=1)

ok = [r for r in rows if 'error' not in r]
changed = [r for r in ok if r.get('changed')]
same = [r for r in ok if not r.get('changed')]
print()
print(f'TOTAL {len(ok)}  changed {len(changed)}  identical {len(same)}')
print()
print('%-12s %-10s %-9s %s' % ('group', 'units', 'has NaN', 'count'))
import collections
tab = collections.Counter()
for r in ok:
    tab[(('CHANGED' if r.get('changed') else 'same'), r.get('units', '?'), (r.get('nan_old', 0) or 0) > 0)] += 1
for k in sorted(tab):
    print('%-12s %-10s %-9s %d' % (k[0], k[1], k[2], tab[k]))
