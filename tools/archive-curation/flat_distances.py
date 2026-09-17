"""Read-only: for every Organized light session whose camera has a flat set without train cards,
the distance in days from the session's FIRST light to each flat set's [first, last] DATE-OBS span.
Mirrors what the resolver sees: session.Lights[0] against a flat group's epoch."""
import os
from datetime import datetime, timezone
from astropy.io import fits

R = 'D:/Astro-Organized/'
CAMS = ('ZWO-ASI585MC-Pro', 'ZWO-ASI1600MM-Pro', 'ZWO-ASI294MC')


def when(h):
    s = str(h.get('DATE-OBS')).replace('Z', '')
    return datetime.fromisoformat(s[:26]).replace(tzinfo=timezone.utc)


def fits_in(d):
    return sorted(f for f in os.listdir(d) if f.lower().endswith('.fits'))


def span(d):
    ts = [when(fits.getheader(os.path.join(d, f))) for f in fits_in(d)]
    return min(ts), max(ts)


for cam in CAMS:
    flats = []
    for dp, dn, fn in os.walk(R + 'flats/' + cam):
        if dp.replace('\\', '/').endswith('/FLAT'):
            h = fits.getheader(os.path.join(dp, fits_in(dp)[0]))
            s, e = span(dp)
            flats.append((dp[len(R + 'flats/' + cam) + 1:].replace('\\', '/'), s, e, h.get('CCD-TEMP'), str(h.get('TELESCOP', '')).strip(), h.get('FOCALLEN')))
    for dp, dn, fn in os.walk(R + 'lights/' + cam):
        names = fits_in(dp) if any(f.lower().endswith('.fits') for f in fn) else []
        if not names:
            continue
        ts = sorted((when(fits.getheader(os.path.join(dp, f))), f) for f in names)
        first = ts[0][0]
        h = fits.getheader(os.path.join(dp, ts[0][1]))
        print(f"{dp[len(R + 'lights/'):].replace(chr(92), '/')}  first={first:%Y-%m-%d %H:%M}Z  T={h.get('CCD-TEMP')}  scope={str(h.get('TELESCOP', '')).strip()!r} fl={h.get('FOCALLEN')}")
        for name, s, e, t, sc, fl in flats:
            d = 0.0 if s <= first <= e else min(abs((first - s).total_seconds()), abs((first - e).total_seconds())) / 86400
            print(f"    {name:48s} {s:%Y-%m-%d %H:%M}..{e:%m-%d %H:%M}  T={t}  scope={sc!r} fl={fl}  days={d:7.2f}")
