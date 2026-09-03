"""Gaia DR3 from Vizier's TAP service, cached to disk per field.

Why not the offline ASTAP D50 extract: D50 is capped at 5000 stars/sqr(degree) and truncates
brightest-first, which is invisible until a master detects deeper than the cut. Measured on this
project's own fields, that is exactly what happens -- M33 (detection dies at BP 15) reads 99.9%
matched against D50, while Orion (detection reaches BP 17) does not, and the two explanations
"the metric is scoring nebulosity" and "the catalogue ran out" are indistinguishable without an
untruncated catalogue. Vizier is untruncated, so it separates them.

TAP/ADQL rather than the asu-tsv endpoint: asu applies its own row caps and column conventions
quietly, and a silently truncated catalogue is the exact failure being diagnosed.
"""
import hashlib, os, urllib.parse, urllib.request
import numpy as np

TAP = 'https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync'
GAIA = 'I/355/gaiadr3'
CACHE_DIR = os.environ.get('TIANWEN_GAIA_CACHE',
                          os.path.join(os.path.expanduser('~'), '.tianwen', 'gaia-cache'))


def fetch_box(ra0, dec0, width_deg, height_deg, mag_max=21.0, table=GAIA, timeout=600):
    """Gaia DR3 in a sky box. Returns (ra_deg, dec_deg, bp_mag). Cached by query.

    width_deg is in COORDINATE degrees of RA, not true angle. CDS reads ADQL BOX that way,
    and passing true angle narrows the box by 1/cos(dec): at +30.7 deg that silently dropped
    10 percent of a field's stars and read as 'a deeper catalogue matched FEWER', which is
    impossible and is the only reason the bug was caught.
    """
    adql = (f'SELECT RAJ2000, DEJ2000, BPmag FROM "{table}" WHERE '
            f'CONTAINS(POINT(\'ICRS\',RAJ2000,DEJ2000), '
            f'BOX(\'ICRS\',{ra0},{dec0},{width_deg},{height_deg}))=1 AND BPmag < {mag_max}')
    key = hashlib.sha1(adql.encode()).hexdigest()[:16]
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, f'gaia_{key}.npz')
    if os.path.exists(path):
        z = np.load(path)
        return z['ra'], z['dec'], z['mag']
    body = urllib.parse.urlencode({'REQUEST': 'doQuery', 'LANG': 'ADQL', 'FORMAT': 'tsv',
                                   'MAXREC': 4000000, 'QUERY': adql}).encode()
    with urllib.request.urlopen(urllib.request.Request(TAP, data=body), timeout=timeout) as r:
        text = r.read().decode('utf-8', 'replace')
    ra, dec, mag = [], [], []
    for line in text.splitlines():
        parts = line.split('\t')
        if len(parts) < 3:
            continue
        try:
            a, d, m = float(parts[0]), float(parts[1]), float(parts[2])
        except ValueError:
            continue          # header rows and units rows
        ra.append(a); dec.append(d); mag.append(m)
    ra, dec, mag = np.array(ra), np.array(dec), np.array(mag)
    np.savez_compressed(path, ra=ra, dec=dec, mag=mag)
    return ra, dec, mag


if __name__ == '__main__':
    import sys
    sys.path.insert(0, r'C:/temp/e2/gaia'); import d50
    ra0, dec0, side = 83.96, -5.39, 0.5
    ra, dec, mag = fetch_box(ra0, dec0, side, side)
    half = side / 2
    a, d, m = d50.read_sky(r'C:/Program Files/astap', 'd50_*.1476',
                           ra0 - half, ra0 + half, dec0 - half, dec0 + half)
    print(f'Vizier Gaia DR3: {len(mag)} stars   ASTAP D50: {len(m)} stars   (0.5x0.5 deg, Orion)\n')
    print(f"{'BP bin':>10} {'Vizier':>9} {'D50':>9} {'D50 keeps':>11}   <- must be ~100% where D50 is complete")
    for lo, hi in [(0,13),(13,14),(14,15),(15,16),(16,17),(17,18),(18,19),(19,20),(20,21)]:
        v = int(((mag >= lo) & (mag < hi)).sum()); c = int(((m >= lo) & (m < hi)).sum())
        if v:
            print(f'{f"{lo}-{hi}":>10} {v:9d} {c:9d} {100*c/v:10.0f}%')
