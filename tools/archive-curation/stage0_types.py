"""Stage 0 over the validator's gap: read the type card that EXISTS, and drop products.

The validator judged salvageability off IMAGETYP and INSTRUME. On this archive both are the wrong
cards for the pre-NINA years:

  FRAMETYP   SharpCap 4.x writes the frame type here, not in IMAGETYP. TianWen's own reader
             already falls back to it (Image.Fits.cs). SharpCap 3.x (SWCREATE='SharpCap', no
             version) writes neither, and only those frames need pixels to classify.
  INSTRUME   Astro Pixel Processor PRESERVES it on every calibrated, registered, cropped and
             extracted frame it writes, so "a camera is named" does not mean "a raw capture".

Product markers, all header-based (folder names are reported as corroboration, never used):
  SOFTWARE 'Astro Pixel Processor...', CALFRAME / CALLIGHT cards     APP output
  SKIPPED card, or a name starting 'Stack_'                          SharpCap live stack
  INSTRUME 'Loaded from fits file'                                   SharpCap re-save
  NAXIS3                                                             a colour cube, not a mosaic
  BITPIX < 0                                                         float, never a raw capture here
  'WithDisplayStretch' in the name                                   a stretched snapshot

READ ONLY. Writes C:/temp/e2/stage0-gap.csv and prints the summary.
"""
import csv
import collections
import os
import astropy.io.fits as fits

LEDGER = r'C:/temp/e2/archive-ledger.csv'
OUT = r'C:/temp/e2/stage0-gap.csv'

rows = [r for r in csv.DictReader(open(LEDGER, encoding='utf-8'))
        if r['verdict'] == 'SALVAGEABLE' and r['reachable'] == 'False']
print(len(rows), 'gap frames to re-read', flush=True)


def product_reason(h, name):
    soft = str(h.get('SOFTWARE', '') or '')
    if soft.startswith('Astro Pixel Processor') or 'CALFRAME' in h or 'CALLIGHT' in h:
        return 'app-output'
    if 'SKIPPED' in h or name.lower().startswith('stack_'):
        return 'live-stack'
    if str(h.get('INSTRUME', '')).strip() == 'Loaded from fits file':
        return 'resave'
    if 'withdisplaystretch' in name.lower():
        return 'stretched'
    if h.get('NAXIS', 0) >= 3 or 'NAXIS3' in h:
        return 'colour-cube'
    if int(h.get('BITPIX', 16)) < 0:
        return 'float'
    return ''


def norm_type(raw):
    t = raw.strip().lower().replace(' ', '').replace('_', '')
    if not t:
        return ''
    for key, val in (('darkflat', 'DARKFLAT'), ('flatdark', 'DARKFLAT'), ('bias', 'BIAS'),
                     ('offset', 'BIAS'), ('dark', 'DARK'), ('flat', 'FLAT'), ('light', 'LIGHT')):
        if key in t:
            return val
    return 'OTHER:' + raw.strip()


verdicts = collections.Counter()
typed = collections.Counter()
raw_types = collections.Counter()
untyped_sets = collections.defaultdict(lambda: {'n': 0, 'gib': 0.0})
untyped_by = collections.Counter()
n = 0
with open(OUT, 'w', newline='', encoding='utf-8') as fh:
    w = csv.writer(fh)
    w.writerow(['path', 'stage0', 'type', 'frametyp_raw', 'swcreate', 'instrume', 'exptime', 'gain',
                'blklevel', 'naxis1', 'naxis2', 'bayerpat', 'ccdtemp', 'size'])
    for r in rows:
        n += 1
        if n % 2000 == 0:
            print(f'  {n} re-read...', flush=True)
        p = r['path']
        name = os.path.basename(p)
        try:
            h = fits.getheader(p)
        except Exception:
            verdicts['unreadable'] += 1
            continue
        pr = product_reason(h, name)
        ft_raw = str(h.get('IMAGETYP', '') or h.get('FRAMETYP', '') or '')
        ft = norm_type(ft_raw)
        if pr:
            stage0 = 'PRODUCT:' + pr
            verdicts[stage0] += 1
        elif ft:
            stage0 = 'TYPED'
            typed[ft] += 1
            raw_types[ft_raw.strip()] += 1
        else:
            stage0 = 'UNTYPED'
            top = os.path.relpath(p, r'D:/Astro-Pics').split(os.sep)[0]
            untyped_by[(top, r['instrume'])] += 1
            key = (os.path.dirname(p), r['instrume'], r['exptime'], str(h.get('GAIN', '')),
                   h.get('NAXIS1'), h.get('NAXIS2'))
            untyped_sets[key]['n'] += 1
            untyped_sets[key]['gib'] += int(r['size']) / 2**30
        verdicts[stage0.split(':')[0]] += 0
        w.writerow([p, stage0, ft, ft_raw.strip(), str(h.get('SWCREATE', '')), r['instrume'],
                    r['exptime'], h.get('GAIN', ''), h.get('BLKLEVEL', ''), h.get('NAXIS1'),
                    h.get('NAXIS2'), h.get('BAYERPAT', ''), h.get('CCD-TEMP', ''), r['size']])

print()
print(f'gap frames re-read: {n}')
prod = sum(c for k, c in verdicts.items() if k.startswith('PRODUCT:'))
print(f'  PRODUCTS (were counted salvageable): {prod}')
for k, c in sorted(verdicts.items()):
    if k.startswith('PRODUCT:'):
        print(f'     {k:28s} {c}')
print(f'  TYPED by IMAGETYP/FRAMETYP:          {sum(typed.values())}')
for k, c in typed.most_common():
    print(f'     {k:28s} {c}')
print('     raw card values:', ', '.join(f'{k!r} x{c}' for k, c in raw_types.most_common(12)))
nu = sum(v['n'] for v in untyped_sets.values())
print(f'  UNTYPED, needs pixels:               {nu} frames in {len(untyped_sets)} capture sets '
      f'({sum(v["gib"] for v in untyped_sets.values()):.1f} GiB)')
for (top, inst), c in sorted(untyped_by.items()):
    print(f'     {top:28s} {inst:28s} {c}')
print()
print('ledger:', OUT)
