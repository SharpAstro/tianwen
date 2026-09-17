"""Heat or light? Use the camera's own darks at one gain, which need no gain model. READ ONLY.

Dark current grows in proportion to exposure (at a given temperature), so every ASI462MC dark set at
gain 80-85 should sit on one rate once temperature is accounted for. A set that light reached
stands off that line. Levels come from the #34 cache; temperatures are read from the sampled frames.
"""
import json
import os
import astropy.io.fits as fits

levels = json.load(open(r'C:/temp/e2/cache-levels.json', encoding='utf-8'))
BIAS = {'80': 14.0, '85': 19.0}   # the matched 0 s sets' medians from the #34 ledger

rows = []
for k, vals in levels.items():
    stage, d, inst, exp, gain, n1, n2 = k.split('|')
    if inst != 'ZWO ASI462MC' or gain not in BIAS or float(exp or 0) <= 0.001:
        continue
    kind = 'dark' if '\\darks\\' in d.lower() else 'light'
    # the three picks, in the same order classify_sets.py used
    import csv
    ps = sorted((r['path'] for r in csv.DictReader(open(r'C:/temp/e2/stage0-gap.csv', encoding='utf-8'))
                 if os.path.dirname(r['path']) == d and r['exptime'] == exp and r['gain'] == gain), key=str.lower)
    picks = [ps[0], ps[len(ps) // 2], ps[-1]] if len(ps) >= 3 else ps
    for p, v in zip(picks, vals):
        t = fits.getheader(p).get('CCD-TEMP')
        excess = v[0] - BIAS[gain]
        rows.append((kind, float(exp), gain, t, excess, excess / float(exp), os.path.relpath(d, 'D:/Astro-Pics')))

rows.sort(key=lambda r: (r[0], r[2], r[1], r[3] or 0))
print(f"{'kind':5s} {'exp':>5s} {'gain':>4s} {'temp':>5s} {'excess':>7s} {'ADU/s':>7s}  set")
for kind, exp, gain, t, ex, rate, d in rows:
    print(f"{kind:5s} {exp:5.1f} {gain:>4s} {t if t is not None else float('nan'):5.1f} {ex:7.0f} {rate:7.1f}  {d[-60:]}")
