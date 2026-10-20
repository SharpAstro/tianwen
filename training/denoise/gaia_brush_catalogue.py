"""The star list the starless plate's brush removes (docs/plans/star-remover-training.md, R0's brush): Gaia DR3 over a
master's field, from Vizier's TAP service, written as a CSV `tianwen dataset starless-plates --brush --catalogue-dir`
reads. Glue only: this fetches the catalogue and writes rows; the C# places them through the master's own WCS and decides
everything else.

Columns: ra_deg, dec_deg (propagated to the master's DATE-OBS by the proper motion), gmag, moving. A row is MOVING when
its proper motion is significant (over 5 sigma, two components) or its parallax is (over 4 sigma): a foreground star,
where a galaxy's own clusters and HII regions sit still. Rows Gaia's DSC calls a galaxy (PGal over 0.5) are dropped.

    python gaia_brush_catalogue.py <master.fits> <out.csv> [--gmax 20.5]
"""
import argparse
import hashlib
import math
import os
import urllib.parse
import urllib.request

import numpy as np
from astropy.io import fits
from astropy.time import Time
from astropy.wcs import WCS

TAP = 'https://tapvizier.cds.unistra.fr/TAPVizieR/tap/sync'
CACHE_DIR = os.environ.get('TIANWEN_GAIA_CACHE', os.path.join(os.path.expanduser('~'), '.tianwen', 'gaia-cache'))
COLUMNS = 'RA_ICRS, DE_ICRS, Gmag, Plx, e_Plx, pmRA, e_pmRA, pmDE, e_pmDE, PGal'


def field_box(header):
    """The master's corners on the sky, as an ADQL BOX in coordinate degrees (Vizier reads its width that way)."""
    wcs = WCS(header, naxis=2)
    w, h = header['NAXIS1'], header['NAXIS2']
    xs = np.array([0, w - 1, 0, w - 1, (w - 1) / 2, 0, w - 1, (w - 1) / 2])
    ys = np.array([0, 0, h - 1, h - 1, 0, (h - 1) / 2, (h - 1) / 2, h - 1])
    ra, dec = wcs.all_pix2world(xs, ys, 0)
    ra0 = float(np.degrees(np.arctan2(np.mean(np.sin(np.radians(ra))), np.mean(np.cos(np.radians(ra))))) % 360)
    dra = (ra - ra0 + 180) % 360 - 180
    dec0 = float((dec.max() + dec.min()) / 2)
    margin = 0.02
    return ra0, dec0, float(2 * np.abs(dra).max() + margin / math.cos(math.radians(dec0))), float(dec.max() - dec.min() + margin)


def fetch(ra0, dec0, width, height, gmax):
    adql = (f'SELECT {COLUMNS} FROM "I/355/gaiadr3" WHERE '
            f"CONTAINS(POINT('ICRS',RA_ICRS,DE_ICRS), BOX('ICRS',{ra0},{dec0},{width},{height}))=1 AND Gmag < {gmax}")
    key = hashlib.sha1(adql.encode()).hexdigest()[:16]
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, f'gaia_brush_{key}.tsv')
    if not os.path.exists(path):
        body = urllib.parse.urlencode({'REQUEST': 'doQuery', 'LANG': 'ADQL', 'FORMAT': 'tsv', 'MAXREC': 4000000,
                                       'QUERY': adql}).encode()
        with urllib.request.urlopen(urllib.request.Request(TAP, data=body), timeout=1800) as r:
            text = r.read().decode('utf-8', 'replace')
        with open(path + '.part', 'w', encoding='utf-8') as f:
            f.write(text)
        os.replace(path + '.part', path)
    rows = []
    with open(path, encoding='utf-8') as f:
        for line in f:
            parts = line.rstrip('\n').split('\t')
            if len(parts) < 10:
                continue
            try:
                ra, dec, g = float(parts[0]), float(parts[1]), float(parts[2])
            except ValueError:
                continue          # the header row
            num = [float(p) if p.strip() else math.nan for p in parts[3:10]]
            rows.append((ra, dec, g, *num))
    return np.array(rows, dtype=np.float64).reshape(-1, 10)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('master')
    ap.add_argument('out')
    ap.add_argument('--gmax', type=float, default=20.5)
    args = ap.parse_args()
    header = fits.getheader(args.master)
    epoch = Time(header['DATE-OBS'][:19], format='isot', scale='utc').jyear if 'DATE-OBS' in header else 2016.0
    ra0, dec0, width, height = field_box(header)
    t = fetch(ra0, dec0, width, height, args.gmax)
    ra, dec, g, plx, eplx, pmra, epmra, pmde, epmde, pgal = t.T
    keep = ~(pgal > 0.5)
    dt = epoch - 2016.0
    has_pm = np.isfinite(pmra) & np.isfinite(pmde)
    ra_e = ra + np.where(has_pm, pmra, 0.0) * dt / 3.6e6 / np.cos(np.radians(dec))
    dec_e = dec + np.where(has_pm, pmde, 0.0) * dt / 3.6e6
    with np.errstate(invalid='ignore', divide='ignore'):
        pm_sig = np.hypot(pmra / epmra, pmde / epmde)
        plx_sig = plx / eplx
    moving = (np.nan_to_num(pm_sig) > 5.0) | (np.nan_to_num(plx_sig) > 4.0)
    with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('ra_deg,dec_deg,gmag,moving\n')
        for a, d, m, mv in zip(ra_e[keep], dec_e[keep], g[keep], moving[keep]):
            f.write(f'{a:.7f},{d:.7f},{m:.3f},{int(mv)}\n')
    print(f'{args.master}: epoch {epoch:.2f}, box {ra0:.4f} {dec0:+.4f} {width:.3f} x {height:.3f} deg, '
          f'{len(t)} Gaia rows, {int((~keep).sum())} dropped as galaxies, {int(keep.sum())} written, '
          f'{int(moving[keep].sum())} moving -> {args.out}')


if __name__ == '__main__':
    main()
