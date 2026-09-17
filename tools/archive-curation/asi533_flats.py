"""Read-only: the ASI533MC Pro flat sets in Astro-Unsorted (the SharpCap era), with the cards the
resolver sees, against the first light of each Unsorted ASI533 session the flat-train fix moved."""
import os
from datetime import datetime, timezone
from astropy.io import fits

U = 'D:/Astro-Unsorted/'
FLATS = [
    'Eta Car Neb SY135mm/SY135mm_Cal/2022-12-24/Flat',
    'Vela SNR/ASI533mc cal/2024-02-10/Flat',
    'ASI55mc Cal Jun/2024-06-06/Flat',
    'ASI55mc Cal Jul Optolong Ultra/2024-07-06/Flat',
    'ASI55mc Cal Jul SII/2024-07-07/Flat',
    'Orion Dec 24/ASI533 120s 121g 2deg/2024-12-08/Flat',
    'Rosette Dec 24/Rosette RGB 120s -5deg 121g 13o/2024-12-30/Flat',
    'ASI533mc -10deg 240s/2025-01-19/Flat',
]
LIGHTS = ['Omega Cen 120s', 'Oph Mol Cloud 120s F2.8 RGB', 'Orion Dec 24/Orion RGB 120s 2deg', 'Rim Nebula 120s F2.8 LPS RGB',
          'Rim Nebula 120s SII 4deg', 'Rosette Dec 24/Rosette RGB 120s -5deg 121g 13o', 'Seagull Nebula', 'Vela SNR']


def when(h):
    return datetime.fromisoformat(str(h.get('DATE-OBS')).replace('Z', '')[:26]).replace(tzinfo=timezone.utc)


def fitsfiles(d):
    out = []
    for dp, dn, fn in os.walk(d):
        if 'proc' in dp.lower():
            continue
        out += [os.path.join(dp, f) for f in fn if f.lower().endswith(('.fits', '.fit'))]
    return sorted(out)


print('== flat sets')
for rel in FLATS:
    fs = fitsfiles(U + rel)
    hs = [fits.getheader(f) for f in (fs[0], fs[-1])]
    h = hs[0]
    print(f"{rel:62s} n={len(fs):3d} {when(hs[0]):%Y-%m-%d %H:%M}..{when(hs[1]):%m-%d %H:%M} "
          f"sw={str(h.get('SWCREATE'))[:14]!r} type={h.get('FRAMETYP') or h.get('IMAGETYP')!r} scope={str(h.get('TELESCOP', '')).strip()!r} "
          f"fl={h.get('FOCALLEN')} filter={h.get('FILTER')!r} exp={h.get('EXPTIME')} T={h.get('CCD-TEMP')} gain={h.get('GAIN')}")

print('== light sessions (first frame per night folder)')
for name in LIGHTS:
    for dp, dn, fn in sorted(os.walk(U + name)):
        low = dp.lower()
        if any(s in low for s in ('proc', 'flat', 'dark', 'bias', 'cal')):
            continue
        fs = sorted(f for f in fn if f.lower().endswith(('.fits', '.fit')))
        if not fs:
            continue
        h = fits.getheader(os.path.join(dp, fs[0]))
        t = (h.get('FRAMETYP') or h.get('IMAGETYP') or '')
        if 'light' not in str(t).lower():
            continue
        print(f"{dp[len(U):]:70s} n={len(fs):3d} first={when(h):%Y-%m-%d %H:%M} scope={str(h.get('TELESCOP', '')).strip()!r} fl={h.get('FOCALLEN')} filter={h.get('FILTER')!r} T={h.get('CCD-TEMP')}")
