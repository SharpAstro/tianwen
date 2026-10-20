"""Group W: the SVBONY SV605CC nights of 2026-09-12 and 2026-10-17 (SH61 EDPH at 270 mm, Optolong L-Quad Enhance), into
D:/Astro-Organized as HARD LINKS.

The owner's captures came in on E:/Astro and were copied into D:/Astro-Pics/2026 on the owner's word (2026-10-20, "you can
copy it surely"), raw frames only and in the owner's own folder layout, each verified by sha256 against its source
(_provenance/ingest-2026-10-20-sv605cc-e-to-pics.csv). This script links them from there: nothing in Astro-Pics is created,
moved or changed, and a link costs no space. Dry run by default; --apply links. Every destination is declared below with
its count, a frame whose type, camera, gain, offset or exposure is not the declared one is REFUSED, an existing destination
is refused, and every link is checked to be the same file as its source afterwards. A manifest records what was done.

  Sculptor Galaxy   2026-09-12  200 x 120 s, 11:19-18:34 UTC; 34 named BAD_ by the owner (cloud, 0003-0010 and 0174-0199)
  Sculptor Galaxy   2026-10-17   90 x 120 s, 11:02-14:18 UTC
  Tarantula Nebula  2026-10-17  108 x 120 s, 14:27-18:15 UTC, and 12 more the owner set aside in a folder of their own,
                                'Tarantula Nebula BAD-UNUSED' (18:26-18:49 UTC, the dawn end): filed with a BAD_ prefix, the one
                                form the bake and the stacker read as a capture rejection (CaptureRejection), so they stay out
                                of every stack and in the archive for later work, as every BAD_ frame does
  FLAT              2026-10-17   46 x 10 s, 10:24-10:31 UTC, shot while the camera cooled from +4 to -5.5 C: condensation on the
                                sensor, repaired at master-flat time (FlatDefects, #1678)
  DARKFLAT          2026-10-17   67 x 10 s, 10:39-10:50 UTC, in the capture's DARK folder but typed DARK with OBJECT FlatWizard
                                and the flats' 10 s: N.I.N.A.'s flat wizard dark-flats, so they file with the flats

Every frame: N.I.N.A. 3.2, INSTRUME 'SVBONY SV605CC', gain 120, offset 20, setpoint -5 C, GRBG, bin 1, FILTER 'Optolong
L-Quad Enhance' carded on every frame (lights, flats and dark-flats), TELESCOP 'SH61 EDPH', FOCALLEN 270. A night is named by
its local EVENING date (the site is UTC+10 in September, UTC+11 from October's first Sunday), the flat set by its own date.

CALIBRATION MEASURED (sv605-calib-measure.py, 2026-10-20), not dated:
  dark    DARK/2026-02-19-g120-o20-t-5-e120s (60), the exact match on every axis; median(dark - bias) 0 / 0 / 0 ADU, so at
          120 s and -5 C the dark is the bias plus its hot pixels. light - dark below zero on at most 0.0014 % of pixels
          over 10 frames across each night (hot pixels that moved since February), p0.01 +160 to +682 ADU
  bias    BIAS/2026-02-19-g120-o20-t-5 (120)
  flat    the night's own set: B/G 1.029 after its dark-flats, inside L-Quad Enhance's 1.023-1.036 on this camera, so the
          filter is confirmed from the flat itself; it serves the September night too (35 days, its train carded)
"""
import argparse
import csv
import glob
import os
import sys
import warnings
from collections import Counter

warnings.filterwarnings('ignore')
from astropy.io import fits  # noqa: E402

PICS = 'D:/Astro-Pics/2026'
CAM = 'SVBONY-SV605CC'
FILT = 'Optolong-L-Quad-Enhance'
# (source folder, destination, IMAGETYP, exposure, declared count, name prefix)
PLAN = [
    (f'{PICS}/2026-09 Sculptor Galaxy/2026-09-12/LIGHT', f'lights/{CAM}/{FILT}/Sculptor-Galaxy/2026-09-12', 'LIGHT', 120.0, 200, ''),
    (f'{PICS}/2026-10-17/LIGHT/Sculptor Galaxy', f'lights/{CAM}/{FILT}/Sculptor-Galaxy/2026-10-17', 'LIGHT', 120.0, 90, ''),
    (f'{PICS}/2026-10-17/LIGHT/Tarantula Nebula', f'lights/{CAM}/{FILT}/Tarantula-Nebula/2026-10-17', 'LIGHT', 120.0, 108, ''),
    (f'{PICS}/2026-10-17/LIGHT/Tarantula Nebula BAD-UNUSED', f'lights/{CAM}/{FILT}/Tarantula-Nebula/2026-10-17', 'LIGHT', 120.0, 12, 'BAD_'),
    (f'{PICS}/2026-10-17/FLAT', f'flats/{CAM}/{FILT}/2026-10-17/FLAT', 'FLAT', 10.0, 46, ''),
    (f'{PICS}/2026-10-17/DARK', f'flats/{CAM}/{FILT}/2026-10-17/DARKFLAT', 'DARK', 10.0, 67, ''),
]
EXPECT = {'INSTRUME': 'SVBONY SV605CC', 'GAIN': 120, 'OFFSET': 20, 'FILTER': 'Optolong L-Quad Enhance', 'BAYERPAT': 'GRBG'}

ap = argparse.ArgumentParser()
ap.add_argument('--root', default='D:/Astro-Organized')
ap.add_argument('--apply', action='store_true')
args = ap.parse_args()

rows, problems = [], []
fresh_dirs = {dst for _, dst, *_ in PLAN}
for dst in fresh_dirs:
    if os.path.exists(os.path.join(args.root, dst)):
        problems.append(f'destination exists: {dst}')
for src_dir, dst, imagetyp, exp, count, prefix in PLAN:
    files = sorted(glob.glob(os.path.join(src_dir, '*.fits')))
    if len(files) != count:
        problems.append(f'{src_dir}: {len(files)} frames, declared {count}')
    for f in files:
        h = fits.getheader(f)
        bad = [f'{k}={h.get(k)!r}' for k, v in EXPECT.items() if h.get(k) != v]
        if str(h.get('IMAGETYP', '')).upper() != imagetyp:
            bad.append(f"IMAGETYP={h.get('IMAGETYP')!r}")
        if abs(float(h.get('EXPTIME', -1)) - exp) > 1e-6:
            bad.append(f"EXPTIME={h.get('EXPTIME')!r}")
        if bad:
            problems.append(f'{f}: ' + ', '.join(bad))
        name = os.path.basename(f)
        filed = name if name.startswith('BAD_') else prefix + name
        rows.append({'source': f.replace('\\', '/'), 'dest': f'{args.root}/{dst}/{filed}', 'kind': imagetyp,
                     'original_name': name, 'object': h.get('OBJECT'), 'date_obs': h.get('DATE-OBS')})

dests = Counter(r['dest'] for r in rows)
problems += [f'two sources for one destination: {d}' for d, n in dests.items() if n > 1]
for d, n in sorted(Counter(os.path.dirname(r['dest']) for r in rows).items()):
    print(f'{n:5d} -> {d}')
if problems:
    print(f'REFUSED, {len(problems)} problem(s):')
    for p in problems[:30]:
        print('  ' + p)
    sys.exit(1)
if not args.apply:
    print(f'{len(rows)} frames to link; dry run, --apply to link')
    sys.exit(0)

for r in rows:
    os.makedirs(os.path.dirname(r['dest']), exist_ok=True)
    os.link(r['source'], r['dest'])
    if not os.path.samefile(r['source'], r['dest']):
        sys.exit(f"link check failed: {r['dest']}")
manifest = os.path.join(args.root, '_provenance', 'organizeW-manifest.csv')
with open(manifest, 'w', newline='') as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0]))
    w.writeheader()
    w.writerows(rows)
print(f'{len(rows)} frames linked, each the same file as its source; manifest {manifest}')
